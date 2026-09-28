namespace StorageXray.Core;

public readonly record struct RectD(double X, double Y, double Width, double Height);
public static class TreemapLayout
{
    public static IReadOnlyList<RectD> Arrange(IReadOnlyList<long> weights, double width, double height)
    {
        var result = new RectD[weights.Count];
        if (width <= 0 || height <= 0 || weights.Count == 0) return result;
        var safe = weights.Select(w => Math.Max(0L, w)).ToArray();
        Split(0, safe.Length, new(0, 0, width, height));
        return result;
        void Split(int start, int end, RectD rect)
        {
            if (start == end) return;
            if (end - start == 1) { result[start] = rect; return; }
            double total = 0; for (int i = start; i < end; i++) total += safe[i];
            if (total <= 0) return;
            double left = 0; int cut = start;
            while (cut < end - 1 && (cut == start || left + safe[cut] <= total / 2)) left += safe[cut++];
            double ratio = left / total;
            if (rect.Width >= rect.Height)
            {
                double w = rect.Width * ratio;
                Split(start, cut, new(rect.X, rect.Y, w, rect.Height));
                Split(cut, end, new(rect.X + w, rect.Y, rect.Width - w, rect.Height));
            }
            else
            {
                double h = rect.Height * ratio;
                Split(start, cut, new(rect.X, rect.Y, rect.Width, h));
                Split(cut, end, new(rect.X, rect.Y + h, rect.Width, rect.Height - h));
            }
        }
    }
}
