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
        <div role="alert" className="alert-warning">
          <p className="font-semibold">
            Creation paused
            {actionState.workflow.failedStep ? ` at ${actionState.workflow.failedStep}` : " after an interrupted step"}
          </p>
          {(actionState.workflow.lastError ?? actionState.error) && (
            <p>{actionState.workflow.lastError ?? actionState.error}</p>
          )}
          <p className="mt-1 text-muted">
            Completed through {actionState.workflow.lastCompletedStep}. Submit again to resume the same workflow;
            no venue or event will be duplicated.
          </p>
        </div>
      )}
      {actionState.error && !actionState.workflow && (
        <p role="alert" className="alert-danger">
          {actionState.error}
        </p>
      )}
      <fieldset className="card flex flex-col gap-4">
        <legend className="px-1 font-semibold">Venue</legend>
        {venues.length > 0 && (
          <div className="flex flex-wrap gap-2 text-sm">
            {(["existing", "new"] as const).map((mode) => (
              <label
                key={mode}
                className="flex min-h-11 cursor-pointer items-center gap-2 rounded-lg border border-border-strong px-3 has-[:checked]:border-primary has-[:checked]:bg-primary-soft"
              >
                <input
                  type="radio"
                  name="venueMode"
                  value={mode}
                  checked={venueMode === mode}
                  onChange={() => setVenueMode(mode)}
                  className="size-4 accent-[var(--primary)]"
                />
                {mode === "existing" ? "Use existing venue" : "Create new venue"}
              </label>
            ))}
          </div>
        )}
        {venueMode === "existing" ? (
          <div className="field">
            <label htmlFor="venueId" className="label">Venue</label>
            <select id="venueId" name="venueId" required className="input">
              {venues.map((v) => (
                <option key={v.id} value={v.id}>
                  {v.name} · {v.city}
                </option>
              ))}
            </select>
          </div>
        ) : (
          <>
            <input type="hidden" name="venueMode" value="new" />
            <TextField name="venueName" label="Venue name" required />
            <TextField name="venueStreet" label="Street" required />
            <div className="grid gap-4 sm:grid-cols-3">
              <TextField name="venueCity" label="City" required />
              <TextField name="venueCountry" label="Country" required />
              <TextField name="venuePostalCode" label="Postal code" hint="Optional" />
            </div>
          </>
        )}
      </fieldset>

      <fieldset className="card flex flex-col gap-4">
        <legend className="px-1 font-semibold">Event</legend>
        <div className="grid gap-4 sm:grid-cols-2">
          <TextField name="title" label="Title" required />
          <TextField name="artist" label="Artist" required />
        </div>
        <div className="field">
          <label htmlFor="description" className="label">Description</label>
          <textarea id="description" name="description" required rows={3} className="input py-2" />
        </div>
        <div className="grid gap-4 sm:grid-cols-2">
          <div className="field">
            <label htmlFor="startsAtLocal" className="label">Starts at</label>
            <input id="startsAtLocal" ref={startsAtRef} type="datetime-local" name="startsAtLocal" required className="input" />
          </div>
          <div className="field">
            <label htmlFor="endsAtLocal" className="label">Ends at</label>
            <input id="endsAtLocal" ref={endsAtRef} type="datetime-local" name="endsAtLocal" required className="input" />
          </div>
        </div>
        <p className="hint">Times are in your browser&apos;s time zone and stored with it.</p>
      </fieldset>

      <fieldset className="card flex flex-col gap-3">
        <legend className="px-1 font-semibold">Sections &amp; pricing (VND)</legend>
        <div aria-hidden="true" className="hidden grid-cols-[1fr_7rem_9rem_auto] gap-2 text-xs font-medium text-muted sm:grid">
          <span>Section name</span>
          <span>Capacity</span>
          <span>Price per seat</span>
          <span className="w-20" />
        </div>
        {sections.map((s, index) => (
          <div key={s.key} className="grid grid-cols-2 gap-2 sm:grid-cols-[1fr_7rem_9rem_auto]">
            <label className="col-span-2 sm:col-span-1">
              <span className="sr-only">Section {index + 1} name</span>
              <input
                placeholder="Section name"
                value={s.name}
                onChange={(e) => updateSection(s.key, "name", e.target.value)}
                name="sectionName"
                required
                className="input"
              />
            </label>
            <label>
              <span className="sr-only">Section {index + 1} capacity</span>
              <input
                placeholder="Capacity"
                type="number"
                min="1"
                inputMode="numeric"
                value={s.capacity}
                onChange={(e) => updateSection(s.key, "capacity", e.target.value)}
                name="sectionCapacity"
                required
                className="input"
              />
            </label>
            <label>
              <span className="sr-only">Section {index + 1} price per seat</span>
              <input
                placeholder="Price"
                type="number"
                min="0"
                inputMode="numeric"
                value={s.price}
                onChange={(e) => updateSection(s.key, "price", e.target.value)}
                name="sectionPrice"
                required
                className="input"
              />
            </label>
            <button
              type="button"
              onClick={() => removeSection(s.key)}
              disabled={sections.length === 1}
              aria-label={`Remove section ${index + 1}`}
              className="btn-ghost col-span-2 w-full sm:col-span-1 sm:w-20"
            >
              Remove
            </button>
          </div>
        ))}
        <button type="button" onClick={addSection} className="btn-secondary w-fit">
          + Add section
        </button>
      </fieldset>

      <div className="flex justify-end">
        <button disabled={pending} type="submit" className="btn-primary" aria-busy={pending}>
          {pending
            ? "Working…"
            : actionState.workflow?.canResume
              ? `Resume ${actionState.workflow.failedStep ?? "workflow"}`
              : "Create & publish event"}
        </button>
      </div>
    </form>
  );
}

function TextField({ name, label, required, hint }: { name: string; label: string; required?: boolean; hint?: string }) {
  return (
    <div className="field">
      <label htmlFor={name} className="label">
        {label}
        {hint && <span className="ml-1 font-normal text-muted">({hint})</span>}
      </label>
      <input id={name} name={name} required={required} className="input" />
    </div>
  );
}
