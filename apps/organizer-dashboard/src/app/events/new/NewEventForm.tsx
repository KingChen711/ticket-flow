"use client";

import { useState } from "react";
import type { VenueResponse } from "@ticketflow/types";
import { createEventAction } from "./actions";

interface SectionRow {
  key: number;
  name: string;
  capacity: string;
  price: string;
}

let nextKey = 0;

export function NewEventForm({ venues }: { venues: VenueResponse[] }) {
  const [venueMode, setVenueMode] = useState<"existing" | "new">(
    venues.length > 0 ? "existing" : "new",
  );
  const [sections, setSections] = useState<SectionRow[]>([
    { key: nextKey++, name: "General", capacity: "50", price: "450000" },
  ]);

  function addSection() {
    setSections((prev) => [...prev, { key: nextKey++, name: "", capacity: "", price: "" }]);
  }

  function removeSection(key: number) {
    setSections((prev) => prev.filter((s) => s.key !== key));
  }

  function updateSection(key: number, field: keyof Omit<SectionRow, "key">, value: string) {
    setSections((prev) => prev.map((s) => (s.key === key ? { ...s, [field]: value } : s)));
  }

  return (
    <form action={createEventAction} className="flex flex-col gap-6">
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
            <input type="datetime-local" name="startsAt" required className="mt-1 w-full rounded border px-3 py-2" />
          </label>
          <label className="flex-1 text-sm">
            Ends at
            <input type="datetime-local" name="endsAt" required className="mt-1 w-full rounded border px-3 py-2" />
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

      <button type="submit" className="w-fit rounded bg-black px-4 py-2 text-white">
        Create &amp; publish event
      </button>
    </form>
  );
}
