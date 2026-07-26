import { HttpError } from "@ticketflow/http-client";
import { parseMyOrderView } from "@ticketflow/types";
import { notFound, redirect } from "next/navigation";
import { auth } from "@/auth";
import { fetchGateway } from "@/lib/gateway";
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
    <div className="mx-auto max-w-md p-6">
      <h1 className="text-xl font-bold">Order {order.id.slice(0, 8)}</h1>
      <p className="text-sm text-gray-600">{order.seatIds.length} seat(s) — {order.totalAmount.toLocaleString()} {order.currency}</p>
      <div className="mt-4"><OrderStatus orderId={order.id} initialOrder={order} /></div>
    </div>
  );
}
