import { NextResponse } from "next/server";

// Deliberately a route handler rather than /public/manifest.json: it is served below the scanner
// mount, so an installed PWA remains in /scanner when the customer app is the multi-zone shell.
export function GET() {
  return new NextResponse(JSON.stringify({
    name: "TicketFlow — Gate Scanner",
    short_name: "Gate Scanner",
    description: "Scan ticket QR codes to check attendees in at the gate.",
    start_url: "/scanner/",
    scope: "/scanner/",
    display: "standalone",
    background_color: "#ffffff",
    theme_color: "#000000",
    icons: [
      {
        src: "/scanner-static/icon.svg",
        sizes: "any",
        type: "image/svg+xml",
        purpose: "any",
      },
    ],
  }), {
    headers: {
      "Content-Type": "application/manifest+json; charset=utf-8",
      "Cache-Control": "public, max-age=3600",
    },
  });
}
