"use client";

import { useEffect, useRef, useState, type ChangeEvent } from "react";
import jsQR from "jsqr";
import type { ValidateTicketResponse } from "@ticketflow/types";

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

export function ScannerClient() {
  const videoRef = useRef<HTMLVideoElement>(null);
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const streamRef = useRef<MediaStream | null>(null);
  const rafRef = useRef<number | null>(null);
  const detectorRef = useRef<BarcodeDetectorLike | null>(null);
  const scanningRef = useRef(false);

  const [phase, setPhase] = useState<Phase>("idle");
  const [result, setResult] = useState<ValidateTicketResponse | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (window.BarcodeDetector) {
      detectorRef.current = new window.BarcodeDetector({ formats: ["qr_code"] });
    }
  }, []);

  useEffect(() => () => stopCamera(), []);

  function stopCamera() {
    if (rafRef.current) cancelAnimationFrame(rafRef.current);
    rafRef.current = null;
    streamRef.current?.getTracks().forEach((t) => t.stop());
    streamRef.current = null;
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
    scanningRef.current = false;
    stopCamera();
    try {
      const res = await fetch("/api/validate", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ qrPayload }),
      });
      if (!res.ok) throw new Error(`Validate request failed (${res.status})`);
      setResult((await res.json()) as ValidateTicketResponse);
      setPhase("result");
    } catch (e) {
      setError(e instanceof Error ? e.message : "Scan failed");
      setPhase("idle");
    }
  }

  function loop() {
    if (!scanningRef.current) return;
    const video = videoRef.current;
    if (video && video.readyState >= video.HAVE_CURRENT_DATA && video.videoWidth > 0) {
      decodeFrame(video, video.videoWidth, video.videoHeight).then((text) => {
        if (text && scanningRef.current) {
          submitScan(text);
          return;
        }
        rafRef.current = requestAnimationFrame(loop);
      });
      return;
    }
    rafRef.current = requestAnimationFrame(loop);
  }

  async function startCamera() {
    setError(null);
    try {
      const stream = await navigator.mediaDevices.getUserMedia({
        video: { facingMode: "environment" },
      });
      streamRef.current = stream;
      const video = videoRef.current;
      if (!video) return;
      video.srcObject = stream;
      await video.play();
      setPhase("camera");
      scanningRef.current = true;
      loop();
    } catch (e) {
      setError(e instanceof Error ? e.message : "Could not access camera");
    }
  }

  // Realistic backup for when the camera can't read a code (glare, damaged screen) — also the
  // path used to verify this feature in an environment with no physical camera (real ticket PDF
  // rasterized to an image and fed in here, docs/phase-8-plan.md Commit 8).
  async function handleUpload(e: ChangeEvent<HTMLInputElement>) {
    setError(null);
    const file = e.target.files?.[0];
    e.target.value = "";
    if (!file) return;
    try {
      const bitmap = await createImageBitmap(file);
      const text = await decodeFrame(bitmap, bitmap.width, bitmap.height);
      if (text) {
        await submitScan(text);
      } else {
        setError("No QR code found in this image.");
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : "Could not read image");
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
      {error && <p className="rounded bg-red-50 p-2 text-sm text-red-600">{error}</p>}

      {phase === "camera" ? (
        <div className="flex flex-col gap-2">
          <video ref={videoRef} className="w-full rounded border" playsInline muted />
          <button
            type="button"
            onClick={() => {
              scanningRef.current = false;
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
