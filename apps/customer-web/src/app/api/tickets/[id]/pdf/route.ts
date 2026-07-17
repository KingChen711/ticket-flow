import { auth } from "@/auth";

const GATEWAY_URL = process.env.GATEWAY_URL ?? "http://localhost:8080";

// Streams the PDF through this proxy rather than linking the browser straight to the gateway —
// same BFF reasoning as the other routes here, plus this is the actual answer to "why not a
// MinIO presigned URL" from Commit 1 Phase 8: this route IS the thing that avoids that trap.
export async function GET(_request: Request, { params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const session = await auth();
  if (!session?.accessToken) return new Response("Unauthorized", { status: 401 });

  const res = await fetch(`${GATEWAY_URL}/ticketing/tickets/${id}/pdf`, {
    headers: { Authorization: `Bearer ${session.accessToken}` },
    cache: "no-store",
  });

  if (!res.ok) return new Response(await res.text(), { status: res.status });

  return new Response(res.body, {
    headers: {
      "Content-Type": "application/pdf",
      "Content-Disposition": `inline; filename="ticket-${id}.pdf"`,
    },
  });
}
