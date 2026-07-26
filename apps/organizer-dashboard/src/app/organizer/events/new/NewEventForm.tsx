"use client";

import { useActionState, useEffect, useRef, useState } from "react";
import { browserTimeZone, serializeLocalDateTime, type EventOnboardingWorkflowResponse, type VenueResponse } from "@ticketflow/types";
import { createEventAction } from "./actions";

interface SectionRow {
  key: number;
  name: string;
  capacity: string;
  price: string;
}

let nextKey = 0;

export function NewEventForm({
  venues,
  creationKey,
  initialWorkflow,
}: {
  venues: VenueResponse[];
  creationKey: string;
  initialWorkflow: EventOnboardingWorkflowResponse | null;
}) {
  const [actionState, formAction, pending] = useActionState(createEventAction, {
    workflow: initialWorkflow,
    error: initialWorkflow?.lastError ?? null,
  });
  const [venueMode, setVenueMode] = useState<"existing" | "new">(
    venues.length > 0 ? "existing" : "new",
  );
  const [sections, setSections] = useState<SectionRow[]>([
    { key: nextKey++, name: "General", capacity: "50", price: "450000" },
  ]);
  const startsAtRef = useRef<HTMLInputElement>(null);
  const endsAtRef = useRef<HTMLInputElement>(null);
  const startsAtUtcRef = useRef<HTMLInputElement>(null);
  const endsAtUtcRef = useRef<HTMLInputElement>(null);
  const timeZoneRef = useRef<HTMLInputElement>(null);
  const startOffsetRef = useRef<HTMLInputElement>(null);
  const endOffsetRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    const url = new URL(window.location.href);
    if (url.searchParams.get("creationKey") === creationKey) return;
    url.searchParams.set("creationKey", creationKey);
    window.history.replaceState(null, "", url);
  }, [creationKey]);

  function addSection() {
    setSections((prev) => [...prev, { key: nextKey++, name: "", capacity: "", price: "" }]);
  }

  function removeSection(key: number) {
    setSections((prev) => prev.filter((s) => s.key !== key));
  }

  function updateSection(key: number, field: keyof Omit<SectionRow, "key">, value: string) {
    setSections((prev) => prev.map((s) => (s.key === key ? { ...s, [field]: value } : s)));
  }

  function captureTimezone() {
    if (actionState.workflow) return;
    if (!startsAtRef.current || !endsAtRef.current || !startsAtUtcRef.current || !endsAtUtcRef.current ||
        !timeZoneRef.current || !startOffsetRef.current || !endOffsetRef.current) return;
    const start = serializeLocalDateTime(startsAtRef.current.value);
    const end = serializeLocalDateTime(endsAtRef.current.value);
    startsAtUtcRef.current.value = start.utc;
    endsAtUtcRef.current.value = end.utc;
    timeZoneRef.current.value = browserTimeZone();
    startOffsetRef.current.value = String(start.utcOffsetMinutes);
    endOffsetRef.current.value = String(end.utcOffsetMinutes);
  }

  return (
    <form action={formAction} onSubmit={captureTimezone} noValidate={!!actionState.workflow} className="flex flex-col gap-6">
      <input type="hidden" name="creationKey" value={creationKey} />
      <input ref={startsAtUtcRef} type="hidden" name="startsAtUtc" />
      <input ref={endsAtUtcRef} type="hidden" name="endsAtUtc" />
      <input ref={timeZoneRef} type="hidden" name="timeZoneId" />
      <input ref={startOffsetRef} type="hidden" name="startUtcOffsetMinutes" />
      <input ref={endOffsetRef} type="hidden" name="endUtcOffsetMinutes" />
      {actionState.workflow && (
        <input type="hidden" name="workflowId" value={actionState.workflow.workflowId} />
      )}
      {actionState.workflow?.canResume && (
        <div role="alert" className="rounded border border-amber-400 bg-amber-50 p-3 text-sm">
          <p className="font-semibold">
            Creation paused
            {actionState.workflow.failedStep ? ` at ${actionState.workflow.failedStep}` : " after an interrupted step"}
          </p>
          {(actionState.workflow.lastError ?? actionState.error) && (
            <p>{actionState.workflow.lastError ?? actionState.error}</p>
          )}
          <p className="mt-1 text-gray-600">
            Completed through {actionState.workflow.lastCompletedStep}. Submit again to resume the same workflow;
            no venue or event will be duplicated.
          </p>
        </div>
      )}
      {actionState.error && !actionState.workflow && (
        <p role="alert" className="rounded border border-red-400 bg-red-50 p-3 text-sm text-red-700">
          {actionState.error}
        </p>
      )}
      <fieldset className="flex flex-col gap-2 rounded border p-4">
        <legend className="px-1 text-sm font-semibold">Venue</legend>
        {venues.length > 0 && (
          <div className="flex gap-4 text-sm">
            <label className="flex items-center gap-1">
              <input
                type="radio"
                name="venueMode"
                value="existing"
                checked={venueMode === "existing"}
                onChange={() => setVenueMode("existing")}
              />
              Use existing venue
            </label>
            <label className="flex items-center gap-1">
              <input
                type="radio"
                name="venueMode"
                value="new"
                checked={venueMode === "new"}
                onChange={() => setVenueMode("new")}
              />
              Create new venue
            </label>
          </div>
        )}
        {venueMode === "existing" ? (
          <select name="venueId" required className="rounded border px-3 py-2">
            {venues.map((v) => (
              <option key={v.id} value={v.id}>
                {v.name} — {v.city}
              </option>
            ))}
          </select>
        ) : (
          <>
            <input type="hidden" name="venueMode" value="new" />
            <input name="venueName" placeholder="Venue name" required className="rounded border px-3 py-2" />
            <input name="venueStreet" placeholder="Street" required className="rounded border px-3 py-2" />
            <div className="flex gap-2">
              <input name="venueCity" placeholder="City" required className="flex-1 rounded border px-3 py-2" />
              <input name="venueCountry" placeholder="Country" required className="flex-1 rounded border px-3 py-2" />
            </div>
            <input name="venuePostalCode" placeholder="Postal code (optional)" className="rounded border px-3 py-2" />
          </>
        )}
      </fieldset>

      <fieldset className="flex flex-col gap-2 rounded border p-4">
        <legend className="px-1 text-sm font-semibold">Event</legend>
        <input name="title" placeholder="Title" required className="rounded border px-3 py-2" />
        <input name="artist" placeholder="Artist" required className="rounded border px-3 py-2" />
        <textarea name="description" placeholder="Description" required className="rounded border px-3 py-2" />
        <div className="flex gap-2">
          <label className="flex-1 text-sm">
            Starts at
            <input ref={startsAtRef} type="datetime-local" name="startsAtLocal" required className="mt-1 w-full rounded border px-3 py-2" />
          </label>
          <label className="flex-1 text-sm">
            Ends at
            <input ref={endsAtRef} type="datetime-local" name="endsAtLocal" required className="mt-1 w-full rounded border px-3 py-2" />
          </label>
        </div>
      </fieldset>

      <fieldset className="flex flex-col gap-2 rounded border p-4">
        <legend className="px-1 text-sm font-semibold">Sections &amp; pricing (VND)</legend>
        {sections.map((s) => (
          <div key={s.key} className="flex gap-2">
            <input
              placeholder="Section name"
              value={s.name}
              onChange={(e) => updateSection(s.key, "name", e.target.value)}
              name="sectionName"
              required
              className="flex-1 rounded border px-3 py-2"
            />
            <input
              placeholder="Capacity"
              type="number"
              min="1"
              value={s.capacity}
              onChange={(e) => updateSection(s.key, "capacity", e.target.value)}
              name="sectionCapacity"
              required
              className="w-24 rounded border px-3 py-2"
            />
            <input
              placeholder="Price"
              type="number"
              min="0"
              value={s.price}
              onChange={(e) => updateSection(s.key, "price", e.target.value)}
              name="sectionPrice"
              required
              className="w-28 rounded border px-3 py-2"
            />
            <button
              type="button"
              onClick={() => removeSection(s.key)}
              disabled={sections.length === 1}
              className="rounded border px-2 text-sm disabled:opacity-30"
            >
              Remove
            </button>
          </div>
        ))}
        <button type="button" onClick={addSection} className="w-fit text-sm text-blue-600 underline">
          + Add section
        </button>
      </fieldset>

      <button disabled={pending} type="submit" className="w-fit rounded bg-black px-4 py-2 text-white disabled:opacity-50">
        {pending
          ? "Working…"
          : actionState.workflow?.canResume
            ? `Resume ${actionState.workflow.failedStep ?? "workflow"}`
            : "Create & publish event"}
      </button>
    </form>
  );
}
