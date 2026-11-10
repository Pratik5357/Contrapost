"use client";

import { useRouter } from "next/navigation";
import { useEffect } from "react";

/** Re-renders the server components on an interval, so a page stays fresh without client-side fetching. */
export function AutoRefresh({ seconds }: { seconds: number }) {
  const router = useRouter();
  useEffect(() => {
    const timer = setInterval(() => router.refresh(), seconds * 1000);
    return () => clearInterval(timer);
  }, [router, seconds]);
  return null;
}
