import Link from "next/link";
import { parseMyOrderViews } from "@ticketflow/types";
import { redirect } from "next/navigation";
import { auth } from "@/auth";
import { fetchGateway } from "@/lib/gateway";
import { formatMoney, orderBadge, statusLabel } from "@/lib/status";

export default async function MyOrdersPage() {
  const session = await auth();
  if (!session?.serverAccessToken || session.error) redirect("/api/auth/signin");

  const orders = await fetchGateway("/ordering/orders", parseMyOrderViews, {
    accessToken: session.serverAccessToken,
  });
  return (
    <div className="container-page max-w-3xl">
      <h1 className="text-2xl font-bold tracking-tight">My orders</h1>
      {orders.length === 0 ? (
        <div className="card mt-6 text-center">
          <p className="font-medium">No orders yet.</p>
          <Link href="/" className="btn-primary mt-4">Browse events</Link>
        </div>
      ) : (
        <ul className="mt-6 flex flex-col gap-3">
          {orders.map((order) => (
            <li key={order.id}>
              <Link
                href={`/orders/${order.id}`}
                className="card flex flex-wrap items-center justify-between gap-3 p-4 transition-colors duration-150 hover:border-primary"
              >
                <div>
                  <p className="font-semibold">Order {order.id.slice(0, 8)}</p>
                  <p className="text-sm text-muted">
                    {new Date(order.placedAt).toLocaleString()} · {order.seatIds.length} seat(s) · {formatMoney(order.totalAmount, order.currency)}
                  </p>
                </div>
                <span className={orderBadge(order.status)}>{statusLabel(order.status)}</span>
              </Link>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
