// Shown at once while a page's data loads, so navigation never looks stuck. It fades in after a short
// delay, so fast loads (most of them) don't flash it.
export default function Loading() {
  return (
    <div className="skeleton" aria-busy="true" aria-label="Loading">
      <div className="skeleton-line wide" />
      <div className="skeleton-line" />
      <div className="skeleton-block" />
      <div className="skeleton-block" />
    </div>
  );
}
