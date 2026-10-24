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

  if (secondsLeft === 0) {
    return (
      <p role="alert" className="alert-danger font-semibold">
        Hold expired — go back and select seats again.
      </p>
    );
  }

  return (
    <p role="timer" aria-live="off" className={secondsLeft <= 60 ? "alert-warning" : "alert-info"}>
      Hold expires in{" "}
      <span className="font-mono font-semibold tabular-nums">
        {minutes}:{seconds.toString().padStart(2, "0")}
      </span>
      . Your seats are reserved until then.
    </p>
  );
}
