using System.Globalization;
using System.Text;

namespace Contrapost.Benchmarks;

/// <summary>Writes benchmark results as CSV, a Markdown table and an SVG chart (no plotting dependencies).</summary>
internal static class Report
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static readonly Dictionary<char, string> Colors = new()
    {
        ['A'] = "#2563eb",
        ['B'] = "#d97706",
        ['C'] = "#059669",
    };

    public static string Csv(IEnumerable<Cell> cells)
    {
        var csv = new StringBuilder("strategy,pool,posted,per_second,p50_ms,p99_ms,avg_attempts,gave_up,errors,reconciliation\n");
        foreach (var c in cells)
        {
            csv.AppendLine(Invariant, $"{c.Strategy},{c.Pool},{c.Posted},{c.PerSecond:F1},{c.P50Ms:F2},{c.P99Ms:F2},{c.AvgAttempts:F3},{c.GaveUp},{c.Errors},{c.Reconciliation}");
        }

        return csv.ToString();
    }

    public static string MarkdownTable(IEnumerable<Cell> cells)
    {
        var table = new StringBuilder("| Strategy | Accounts in pool | Transfers/s | p50 | p99 | Avg attempts | Gave up | Reconciliation |\n|---|---:|---:|---:|---:|---:|---:|---|\n");
        foreach (var c in cells)
        {
            table.AppendLine(Invariant, $"| {c.Strategy} | {c.Pool:N0} | {c.PerSecond:N0} | {c.P50Ms:F1} ms | {c.P99Ms:F1} ms | {c.AvgAttempts:F2} | {c.GaveUp} | {c.Reconciliation} |");
        }

        return table.ToString();
    }

    /// <summary>Two panels, throughput and p99 latency, against pool size (fewer accounts = more contention).</summary>
    public static string Svg(IReadOnlyList<Cell> cells, Options options)
    {
        const int width = 920, height = 380, panelWidth = 400, panelHeight = 250, top = 70, left = 70, gap = 80;
        var pools = cells.Select(c => c.Pool).Distinct().Order().ToArray();
        var strategies = cells.Select(c => c.Strategy).Distinct().ToArray();
        var svg = new StringBuilder();
        svg.AppendLine(Invariant, $"""<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}" font-family="system-ui, -apple-system, sans-serif" font-size="12">""");
        svg.AppendLine(Invariant, $"""<rect width="{width}" height="{height}" fill="#ffffff"/>""");
        svg.AppendLine(Invariant, $"""<text x="{left}" y="28" font-size="16" font-weight="600" fill="#111827">Locking strategies under contention</text>""");
        svg.AppendLine(Invariant, $"""<text x="{left}" y="48" fill="#4b5563">{options.Workers} workers, {options.Seconds} s per cell; each transfer picks 2 accounts from the pool. Fewer accounts = more contention.</text>""");

        Panel(0, "Throughput (transfers/s, higher is better)", c => c.PerSecond);
        Panel(1, "p99 latency (ms, lower is better)", c => c.P99Ms);

        // Legend
        var legendY = top + panelHeight + 50;
        for (var i = 0; i < strategies.Length; i++)
        {
            var x = left + i * 170;
            var color = Colors.GetValueOrDefault(strategies[i][0], "#6b7280");
            svg.AppendLine(Invariant, $"""<line x1="{x}" y1="{legendY}" x2="{x + 24}" y2="{legendY}" stroke="{color}" stroke-width="3"/>""");
            svg.AppendLine(Invariant, $"""<text x="{x + 32}" y="{legendY + 4}" fill="#111827">{strategies[i]}</text>""");
        }

        svg.AppendLine("</svg>");
        return svg.ToString();

        void Panel(int index, string title, Func<Cell, double> value)
        {
            var x0 = left + index * (panelWidth + gap);
            var max = Math.Max(1, cells.Max(value)) * 1.1;
            double X(int pool) => x0 + (pools.Length == 1 ? panelWidth / 2.0 : Array.IndexOf(pools, pool) * panelWidth / (double)(pools.Length - 1));
            double Y(double v) => top + panelHeight - v / max * panelHeight;

            svg.AppendLine(Invariant, $"""<text x="{x0}" y="{top - 8}" font-weight="600" fill="#111827">{title}</text>""");
            for (var t = 0; t <= 4; t++)
            {
                var v = max * t / 4;
                svg.AppendLine(Invariant, $"""<line x1="{x0}" y1="{Y(v):F1}" x2="{x0 + panelWidth}" y2="{Y(v):F1}" stroke="#e5e7eb"/>""");
                svg.AppendLine(Invariant, $"""<text x="{x0 - 8}" y="{Y(v) + 4:F1}" text-anchor="end" fill="#6b7280">{v:N0}</text>""");
            }

            foreach (var pool in pools)
            {
                svg.AppendLine(Invariant, $"""<text x="{X(pool):F1}" y="{top + panelHeight + 18}" text-anchor="middle" fill="#6b7280">{pool:N0}</text>""");
            }

            svg.AppendLine(Invariant, $"""<text x="{x0 + panelWidth / 2}" y="{top + panelHeight + 34}" text-anchor="middle" fill="#4b5563">accounts in pool</text>""");

            foreach (var strategy in strategies)
            {
                var color = Colors.GetValueOrDefault(strategy[0], "#6b7280");
                var points = cells.Where(c => c.Strategy == strategy).OrderBy(c => c.Pool).Select(c => (X(c.Pool), Y(value(c)))).ToList();
                svg.AppendLine(Invariant, $"""<polyline fill="none" stroke="{color}" stroke-width="2.5" points="{string.Join(' ', points.Select(p => string.Create(Invariant, $"{p.Item1:F1},{p.Item2:F1}")))}"/>""");
                foreach (var (px, py) in points)
                {
                    svg.AppendLine(Invariant, $"""<circle cx="{px:F1}" cy="{py:F1}" r="3.5" fill="{color}"/>""");
                }
            }
        }
    }
}
