"use client";

import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";

/**
 * Re-renders the page's server components every few seconds and says how fresh the data is. The refresh
 * pauses while the tab is hidden, so a forgotten tab doesn't keep polling the API.
 */
export function LiveStatus({ seconds, renderedAt }: { seconds: number; renderedAt: number }) {
  const router = useRouter();
  const [now, setNow] = useState(renderedAt);

  useEffect(() => {
    const refresh = setInterval(() => {
      if (document.visibilityState === "visible") router.refresh();
    }, seconds * 1000);
    const tick = setInterval(() => setNow(Date.now()), 1000);
    return () => {
      clearInterval(refresh);
      clearInterval(tick);
    };
  }, [router, seconds]);

  const age = Math.max(0, Math.round((now - renderedAt) / 1000));
  return (
    <p className="live" role="status">
      <span className="live-dot" aria-hidden="true" />
      Live · updated {age < 2 ? "just now" : `${age}s ago`}
    </p>
  );
}
