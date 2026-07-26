"use client";

import { useEffect, useRef, useState, type ChangeEvent } from "react";
import jsQR from "jsqr";
import { fetchJson } from "@ticketflow/http-client";
import { parseValidateTicketResponse, type ValidateTicketResponse } from "@ticketflow/types";

// The Shape Detection API's BarcodeDetector isn't in TS's DOM lib and isn't available on every
// browser (probed in this dev environment's desktop Chromium: absent — docs/phase-8-plan.md
// Commit 8. In practice it mostly only ships on Android Chrome/ChromeOS). Used as an optional
// fast native path when present; jsQR (pure JS, no native dependency) is the real fallback that
// makes this work everywhere, exactly the "probe first, fallback if missing" call from the plan.
interface BarcodeDetectorLike {
  detect(source: CanvasImageSource): Promise<{ rawValue: string }[]>;
}
declare global {
  interface Window {
    BarcodeDetector?: new (options?: { formats: string[] }) => BarcodeDetectorLike;
  }
}

type Phase = "idle" | "camera" | "result";

export function ScannerClient({ eventId, gateLabel }: { eventId: string; gateLabel: string }) {
  const videoRef = useRef<HTMLVideoElement>(null);
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const streamRef = useRef<MediaStream | null>(null);
  const rafRef = useRef<number | null>(null);
  const detectorRef = useRef<BarcodeDetectorLike | null>(null);
  const scanningRef = useRef(false);
  const decodeInFlightRef = useRef(false);
  const submitInFlightRef = useRef(false);
  const startingCameraRef = useRef(false);
  const mountedRef = useRef(false);

  const [phase, setPhase] = useState<Phase>("idle");
  const [result, setResult] = useState<ValidateTicketResponse | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    mountedRef.current = true;
    if (window.BarcodeDetector) {
      detectorRef.current = new window.BarcodeDetector({ formats: ["qr_code"] });
    }

    // Decoding a frame can outlive a route transition. Stop immediately when the tab is no
    // longer visible, instead of keeping a physical camera and an animation loop alive behind
    // another app or a locked phone screen.
    const onVisibilityChange = () => {
      if (document.hidden) {
        stopCamera();
        if (mountedRef.current) setPhase("idle");
      }
    };
    document.addEventListener("visibilitychange", onVisibilityChange);

    return () => {
      mountedRef.current = false;
      document.removeEventListener("visibilitychange", onVisibilityChange);
      stopCamera();
    };
  }, []);

  function stopCamera() {
    scanningRef.current = false;
    if (rafRef.current) cancelAnimationFrame(rafRef.current);
    rafRef.current = null;
    streamRef.current?.getTracks().forEach((t) => t.stop());
    streamRef.current = null;
    const video = videoRef.current;
    if (video) video.srcObject = null;
  }

  async function decodeFrame(
    source: CanvasImageSource,
    width: number,
    height: number,
  ): Promise<string | null> {
    if (detectorRef.current) {
      try {
        const codes = await detectorRef.current.detect(source);
        return codes[0]?.rawValue ?? null;
      } catch {
        // Native detector can throw on an unsupported source type — fall through to jsQR.
      }
    }
    const canvas = canvasRef.current;
    const ctx = canvas?.getContext("2d");
    if (!canvas || !ctx) return null;
    canvas.width = width;
    canvas.height = height;
    ctx.drawImage(source, 0, 0, width, height);
    const imageData = ctx.getImageData(0, 0, width, height);
    return jsQR(imageData.data, imageData.width, imageData.height)?.data ?? null;
  }

  async function submitScan(qrPayload: string) {
    if (submitInFlightRef.current) return;
    submitInFlightRef.current = true;
    scanningRef.current = false;
    stopCamera();
    try {
      if (!navigator.onLine) {
        throw new Error("Offline validation is unavailable. Reconnect before scanning a ticket.");
      }
      // This public path remains under the scanner mount. Both the customer shell and an
      // independently deployed scanner rewrite it to the zone-local /api/validate BFF route.
      const response = await fetchJson("/scanner/api/validate", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ qrPayload, eventId }),
        timeoutMs: 5_000,
        validate: parseValidateTicketResponse,
      });
      if (mountedRef.current) {
        setResult(response);
        setPhase("result");
      }
    } catch (e) {
      if (mountedRef.current) {
        setError(e instanceof Error ? e.message : "Scan failed");
        setPhase("idle");
      }
    } finally {
      submitInFlightRef.current = false;
    }
  }

  function loop() {
    if (!scanningRef.current || decodeInFlightRef.current) return;
    const video = videoRef.current;
    if (video && video.readyState >= video.HAVE_CURRENT_DATA && video.videoWidth > 0) {
      decodeInFlightRef.current = true;
      void decodeFrame(video, video.videoWidth, video.videoHeight)
        .then((text) => {
          if (text && scanningRef.current) {
            void submitScan(text);
          }
        })
        .catch((e: unknown) => {
          if (mountedRef.current) {
            setError(e instanceof Error ? e.message : "Could not decode camera frame");
          }
        })
        .finally(() => {
          decodeInFlightRef.current = false;
          if (scanningRef.current) rafRef.current = requestAnimationFrame(loop);
        });
      return;
    }
    rafRef.current = requestAnimationFrame(loop);
  }

  async function startCamera() {
    if (startingCameraRef.current || scanningRef.current || streamRef.current) return;
    startingCameraRef.current = true;
    setError(null);
    try {
      const stream = await navigator.mediaDevices.getUserMedia({
        video: { facingMode: "environment" },
      });
      // The user can leave the page or hide its tab while permission is open. Never retain a
      // stream whose requesting component no longer owns it.
      if (!mountedRef.current || document.hidden) {
        stream.getTracks().forEach((track) => track.stop());
        return;
      }
      const video = videoRef.current;
      if (!video) {
        stream.getTracks().forEach((track) => track.stop());
        return;
      }
      streamRef.current = stream;
      video.srcObject = stream;
      await video.play();
      if (!mountedRef.current || document.hidden || streamRef.current !== stream) {
        stream.getTracks().forEach((track) => track.stop());
        if (video.srcObject === stream) video.srcObject = null;
        return;
      }
      setPhase("camera");
      scanningRef.current = true;
      loop();
    } catch (e) {
      if (mountedRef.current) setError(e instanceof Error ? e.message : "Could not access camera");
    } finally {
      startingCameraRef.current = false;
    }
  }

  // Realistic backup for when the camera can't read a code (glare, damaged screen) — also the
  // path used to verify this feature in an environment with no physical camera (real ticket PDF
  // rasterized to an image and fed in here, docs/phase-8-plan.md Commit 8).
  async function handleUpload(e: ChangeEvent<HTMLInputElement>) {
    if (submitInFlightRef.current || decodeInFlightRef.current) return;
    setError(null);
    const file = e.target.files?.[0];
    e.target.value = "";
    if (!file) return;
    stopCamera();
    try {
      const bitmap = await createImageBitmap(file);
      try {
        const text = await decodeFrame(bitmap, bitmap.width, bitmap.height);
        if (text) {
          await submitScan(text);
        } else if (mountedRef.current) {
          setError("No QR code found in this image.");
        }
      } finally {
        // ImageBitmap owns decoded image memory outside JS heap. It has no useful lifetime after
        // one decode, so release it even if BarcodeDetector or canvas conversion throws.
        bitmap.close();
      }
    } catch (e) {
      if (mountedRef.current) setError(e instanceof Error ? e.message : "Could not read image");
    }
  }

  function scanNext() {
    setResult(null);
    setError(null);
    setPhase("idle");
  }

  if (phase === "result" && result) {
    return <ResultPanel result={result} onScanNext={scanNext} />;
  }

  return (
    <div className="mx-auto flex max-w-md flex-col gap-4 p-6">
      <h1 className="text-xl font-semibold">Scan ticket</h1>
      <p className="rounded bg-blue-50 p-2 text-sm text-blue-900">
        Event {eventId} · Gate {gateLabel}
      </p>
      <p className="text-sm text-gray-600">
        Online validation required. This scanner does not validate tickets offline.
      </p>
      {error && <p className="rounded bg-red-50 p-2 text-sm text-red-600">{error}</p>}

      {phase === "camera" ? (
        <div className="flex flex-col gap-2">
          <video ref={videoRef} className="w-full rounded border" playsInline muted />
          <button
            type="button"
            onClick={() => {
              stopCamera();
              setPhase("idle");
            }}
            className="rounded border px-4 py-2"
          >
            Stop camera
          </button>
        </div>
      ) : (
        <button type="button" onClick={startCamera} className="rounded bg-black px-4 py-2 text-white">
          Start camera scan
        </button>
      )}

      <label className="flex w-fit cursor-pointer flex-col gap-1 text-sm text-blue-600 underline">
        Upload QR image instead
        <input type="file" accept="image/*" onChange={handleUpload} className="hidden" />
      </label>

      <canvas ref={canvasRef} className="hidden" />
    </div>
  );
}

function ResultPanel({
  result,
  onScanNext,
}: {
  result: ValidateTicketResponse;
  onScanNext: () => void;
}) {
  const style = {
    Valid: { bg: "bg-green-600", label: "VALID — checked in" },
    AlreadyCheckedIn: { bg: "bg-yellow-500", label: "ALREADY CHECKED IN" },
    NotFound: { bg: "bg-red-600", label: "NOT FOUND" },
    WrongEvent: { bg: "bg-red-600", label: "WRONG EVENT" },
    Revoked: { bg: "bg-red-600", label: "REVOKED" },
  }[result.status];

  return (
    <div
      className={`flex min-h-[70vh] flex-col items-center justify-center gap-4 p-6 text-white ${style.bg}`}
    >
      <p className="text-3xl font-bold">{style.label}</p>
      {result.checkedInAt && (
        <p className="text-sm opacity-90">
          Checked in at {new Date(result.checkedInAt).toLocaleString()}
        </p>
      )}
      <button type="button" onClick={onScanNext} className="mt-4 rounded bg-white px-6 py-2 font-medium text-black">
        Scan next
      </button>
    </div>
  );
}
