"use client";

import { useEffect, useState } from "react";

// The hold's TTL — reservation-service releases the seats server-side when it expires
// (Redis key TTL, Phase 2); this is just a client-side clock telling the user how long they
// have left, not the source of truth for whether the hold is still valid.
export function Countdown({ expiresAt }: { expiresAt: string }) {
  const [secondsLeft, setSecondsLeft] = useState(() =>
    Math.max(0, Math.round((new Date(expiresAt).getTime() - Date.now()) / 1000)),
  );

  useEffect(() => {
    const interval = setInterval(() => {
      setSecondsLeft(Math.max(0, Math.round((new Date(expiresAt).getTime() - Date.now()) / 1000)));
    }, 1000);
    return () => clearInterval(interval);
  }, [expiresAt]);

  const minutes = Math.floor(secondsLeft / 60);
  const seconds = secondsLeft % 60;

  return (
    <p className={secondsLeft === 0 ? "font-semibold text-red-600" : "text-gray-700"}>
      {secondsLeft === 0
        ? "Hold expired — go back and select seats again."
        : `Hold expires in ${minutes}:${seconds.toString().padStart(2, "0")}`}
    </p>
  );
}
