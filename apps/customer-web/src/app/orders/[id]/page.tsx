import { notFound, redirect } from "next/navigation";
import type { MyOrderView } from "@ticketflow/types";
import { auth } from "@/auth";
import { OrderStatus } from "./OrderStatus";

const GATEWAY_URL = process.env.GATEWAY_URL ?? "http://localhost:8080";

export default async function OrderPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const session = await auth();
  if (!session?.accessToken) redirect("/api/auth/signin");

  const res = await fetch(`${GATEWAY_URL}/ordering/orders/${id}`, {
    headers: { Authorization: `Bearer ${session.accessToken}` },
    cache: "no-store",
  });
  // 404 covers both "doesn't exist" and "belongs to someone else" — ordering doesn't
  // distinguish the two on purpose (Phase 8 Commit 2 ownership check).
  if (res.status === 404) notFound();
  if (!res.ok) throw new Error(`Gateway /ordering/orders/${id} responded ${res.status}`);

  const order = (await res.json()) as MyOrderView;

  return (
    <div className="mx-auto max-w-md p-6">
      <h1 className="text-xl font-bold">Order {order.id.slice(0, 8)}</h1>
      <p className="text-sm text-gray-600">
        {order.seatIds.length} seat(s) — {order.totalAmount.toLocaleString()} {order.currency}
      </p>
      <div className="mt-4">
        <OrderStatus orderId={order.id} initialOrder={order} />
      </div>
    </div>
  );
}
