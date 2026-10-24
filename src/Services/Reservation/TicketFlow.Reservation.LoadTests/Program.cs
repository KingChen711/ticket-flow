// Retired in Phase 12 Commit 4.
//
// This service-local scenario used the public POST /holds/{holdId}/confirm endpoint. That route
// was a checkout bypass: it could mark seats sold without an order, payment, or saga ownership
// proof, so it no longer exists. Keep this project as an explicit pointer for anyone invoking the
// historical command; the supported flash-sale scenario now lives in
// tests/TicketFlow.FlashSale.LoadTests and confirms seats only through Ordering/PurchaseSaga.
Console.WriteLine(
    "Reservation.LoadTests was retired: run tests/TicketFlow.FlashSale.LoadTests for the supported hold -> order -> saga flow.");
