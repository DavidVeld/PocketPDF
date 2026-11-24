namespace PocketPDF.Core;

/// <summary>
/// Finds the areas of an overlay (from <see cref="OverlayCompare"/>) that changed between revisions, to cloud them.
/// The page is cut into square cells; cells with red or blue pixels that touch (also diagonally) form one area.
/// That joins changes up to about one cell apart, like dilating the red/blue mask before flood filling it, but much faster.
/// </summary>
public static class ChangeFinder
{
    /// <summary>How far apart two changes can be and still get one cloud, in points.</summary>
    public const double CellSize = 12;
    /// <summary>Room between the changed lines and the cloud, in points.</summary>
    public const double Padding = 8;
    /// <summary>Areas with less changed ink than this many square points are noise, such as a speck of anti-aliasing.</summary>
    public const double MinimumChangedArea = 1;
    /// <summary>How far red and blue must differ for a pixel to count as changed. The faint tint left after aligning stays under it.</summary>
    const int ColourDifference = 100;

    /// <summary>Changed areas in view points, padded and clamped to the page. Overlapping areas are merged.</summary>
    public static List<ViewRect> FindChangedAreas(PageBitmap overlay, double pixelsPerPoint)
    {
        double cellPixels = CellSize * pixelsPerPoint;
        int columns = (int)Math.Ceiling(overlay.Width / cellPixels), rows = (int)Math.Ceiling(overlay.Height / cellPixels);
        var changedPixels = new int[columns * rows];
        var cellBounds = new (int Left, int Top, int Right, int Bottom)[columns * rows];
        var pixels = overlay.BgraPixels;
        for (int y = 0; y < overlay.Height; y++)
        for (int x = 0; x < overlay.Width; x++)
        {
            int offset = (y * overlay.Width + x) * 4;
            if (Math.Abs(pixels[offset] - pixels[offset + 2]) <= ColourDifference) continue; // blue vs red: equal on black, grey and white
            int cell = (int)(y / cellPixels) * columns + (int)(x / cellPixels);
            cellBounds[cell] = changedPixels[cell]++ == 0 ? (x, y, x, y)
                : (Math.Min(cellBounds[cell].Left, x), Math.Min(cellBounds[cell].Top, y), Math.Max(cellBounds[cell].Right, x), Math.Max(cellBounds[cell].Bottom, y));
        }

        double minimumPixels = Math.Max(3, MinimumChangedArea * pixelsPerPoint * pixelsPerPoint);
        double pageWidth = overlay.Width / pixelsPerPoint, pageHeight = overlay.Height / pixelsPerPoint;
        var areas = new List<ViewRect>();
        var visited = new bool[columns * rows];
        var cellsToVisit = new Stack<int>();
        for (int startCell = 0; startCell < changedPixels.Length; startCell++)
        {
            if (changedPixels[startCell] == 0 || visited[startCell]) continue;
            // Flood fill one group of touching cells.
            int groupPixels = 0;
            var bounds = cellBounds[startCell];
            visited[startCell] = true;
            cellsToVisit.Push(startCell);
            while (cellsToVisit.Count > 0)
            {
                int cell = cellsToVisit.Pop();
                groupPixels += changedPixels[cell];
                var cellBox = cellBounds[cell];
                bounds = (Math.Min(bounds.Left, cellBox.Left), Math.Min(bounds.Top, cellBox.Top), Math.Max(bounds.Right, cellBox.Right), Math.Max(bounds.Bottom, cellBox.Bottom));
                int column = cell % columns, row = cell / columns;
                for (int neighbourRow = Math.Max(0, row - 1); neighbourRow <= Math.Min(rows - 1, row + 1); neighbourRow++)
                for (int neighbourColumn = Math.Max(0, column - 1); neighbourColumn <= Math.Min(columns - 1, column + 1); neighbourColumn++)
                {
                    int neighbour = neighbourRow * columns + neighbourColumn;
                    if (changedPixels[neighbour] == 0 || visited[neighbour]) continue;
                    visited[neighbour] = true;
                    cellsToVisit.Push(neighbour);
                }
            }
            if (groupPixels < minimumPixels) continue;
            var area = new ViewRect(bounds.Left / pixelsPerPoint, bounds.Top / pixelsPerPoint, (bounds.Right + 1) / pixelsPerPoint, (bounds.Bottom + 1) / pixelsPerPoint)
                .Inflate(Padding);
            areas.Add(new ViewRect(Math.Max(0, area.Left), Math.Max(0, area.Top), Math.Min(pageWidth, area.Right), Math.Min(pageHeight, area.Bottom)));
        }
        return MergeOverlapping(areas);
    }

    /// <summary>Padding can make neighbouring areas overlap; overlapping clouds look messy, so they become one.</summary>
    internal static List<ViewRect> MergeOverlapping(List<ViewRect> areas)
    {
        for (int i = 0; i < areas.Count; i++)
        for (int j = i + 1; j < areas.Count; j++)
        {
            var (first, second) = (areas[i], areas[j]);
            if (first.Left > second.Right || second.Left > first.Right || first.Top > second.Bottom || second.Top > first.Bottom) continue;
            areas[i] = new ViewRect(Math.Min(first.Left, second.Left), Math.Min(first.Top, second.Top), Math.Max(first.Right, second.Right), Math.Max(first.Bottom, second.Bottom));
            areas.RemoveAt(j);
            j = i; // the grown area may now overlap ones already checked
        }
        return areas;
    }
}
