import { auth } from "@/auth";
import { ScannerClient } from "./ScannerClient";

// Same role gate as organizer-dashboard (docs/phase-8-plan.md Commit 7/8) — POST
// /tickets/validate is RequireOrganizer at the backend, this page refuses to render the scanner
// for a signed-in customer instead of letting every scan attempt 403.
export default async function ScannerPage() {
  const session = await auth();

  if (!session) {
    return (
      <div className="mx-auto max-w-md p-6">
        <p className="text-gray-500">Sign in with an organizer account to scan tickets.</p>
      </div>
    );
  }

  if (!session.roles?.includes("organizer")) {
    return (
      <div className="mx-auto max-w-md p-6">
        <p className="text-red-600">
          This app is for organizers only. Your account does not have the organizer role.
        </p>
      </div>
    );
  }

  return <ScannerClient />;
}
