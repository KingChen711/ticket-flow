import Link from "next/link";
import { parseMyOrderViews } from "@ticketflow/types";
import { redirect } from "next/navigation";
import { auth } from "@/auth";
import { fetchGateway } from "@/lib/gateway";

export default async function MyOrdersPage() {
  const session = await auth();
  if (!session?.serverAccessToken || session.error) redirect("/api/auth/signin");

  const orders = await fetchGateway("/ordering/orders", parseMyOrderViews, {
    accessToken: session.serverAccessToken,
  });
  return (
    <div className="mx-auto max-w-2xl p-6">
      <h1 className="text-xl font-bold">My orders</h1>
      {orders.length === 0 ? <p className="mt-4 text-gray-500">No orders yet.</p> : (
        <ul className="mt-4 flex flex-col gap-2">
          {orders.map((order) => (
            <li key={order.id}>
              <Link href={`/orders/${order.id}`} className="flex items-center justify-between rounded border p-3 text-sm hover:bg-gray-50">
                <span>{order.seatIds.length} seat(s) — {order.totalAmount.toLocaleString()} {order.currency}</span>
                <span className="font-medium">{order.status}</span>
              </Link>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
