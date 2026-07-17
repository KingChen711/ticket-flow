import Link from "next/link";
import { redirect } from "next/navigation";
import type { MyOrderView } from "@ticketflow/types";
import { auth } from "@/auth";

const GATEWAY_URL = process.env.GATEWAY_URL ?? "http://localhost:8080";

// GET /ordering/orders — identity from claims (sub), no query param (Phase 8 Commit 2).
export default async function MyOrdersPage() {
  const session = await auth();
  if (!session?.accessToken) redirect("/api/auth/signin");

  const res = await fetch(`${GATEWAY_URL}/ordering/orders`, {
    headers: { Authorization: `Bearer ${session.accessToken}` },
    cache: "no-store",
  });
  if (!res.ok) throw new Error(`Gateway /ordering/orders responded ${res.status}`);

  const orders = (await res.json()) as MyOrderView[];

  return (
    <div className="mx-auto max-w-2xl p-6">
      <h1 className="text-xl font-bold">My orders</h1>
      {orders.length === 0 ? (
        <p className="mt-4 text-gray-500">No orders yet.</p>
      ) : (
        <ul className="mt-4 flex flex-col gap-2">
          {orders.map((order) => (
            <li key={order.id}>
              <Link href={`/orders/${order.id}`} className="flex items-center justify-between rounded border p-3 text-sm hover:bg-gray-50">
                <span>
                  {order.seatIds.length} seat(s) — {order.totalAmount.toLocaleString()} {order.currency}
                </span>
                <span className="font-medium">{order.status}</span>
              </Link>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
