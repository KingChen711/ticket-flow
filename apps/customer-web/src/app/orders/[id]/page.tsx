import { HttpError } from "@ticketflow/http-client";
import { parseMyOrderView } from "@ticketflow/types";
import { notFound, redirect } from "next/navigation";
import { auth } from "@/auth";
import { fetchGateway } from "@/lib/gateway";
import Link from "next/link";
import { formatMoney } from "@/lib/status";
import { OrderStatus } from "./OrderStatus";

export default async function OrderPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const session = await auth();
  if (!session?.serverAccessToken || session.error) redirect("/api/auth/signin");

  let order;
  try {
    order = await fetchGateway(`/ordering/orders/${id}`, parseMyOrderView, {
      accessToken: session.serverAccessToken,
    });
  } catch (error) {
    if (error instanceof HttpError && error.status === 404) notFound();
    throw error;
  }

  return (
    <div className="container-page max-w-lg">
      <Link href="/orders" className="link text-sm">← My orders</Link>
      <h1 className="mt-4 text-2xl font-bold tracking-tight">Order {order.id.slice(0, 8)}</h1>
      <p className="text-sm text-muted">{order.seatIds.length} seat(s) · {formatMoney(order.totalAmount, order.currency)}</p>
      <div className="card mt-6"><OrderStatus orderId={order.id} initialOrder={order} /></div>
    </div>
  );
}
