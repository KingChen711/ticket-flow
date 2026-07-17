import { NextResponse } from "next/server";
import { auth } from "@/auth";

const GATEWAY_URL = process.env.GATEWAY_URL ?? "http://localhost:8080";

export async function GET(_request: Request, { params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const session = await auth();
  if (!session?.accessToken) return NextResponse.json({ error: "unauthorized" }, { status: 401 });

  const res = await fetch(`${GATEWAY_URL}/ticketing/orders/${id}/tickets`, {
    headers: { Authorization: `Bearer ${session.accessToken}` },
    cache: "no-store",
  });

  return NextResponse.json(await res.json(), { status: res.status });
}
