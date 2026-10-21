using Mosaic.Core;

namespace Mosaic.Output;

public static class CalibrationFixture
{
    public static LayoutPlan Layout(PaperSpec paper)
    {
        // Always four sheets. Resolve Auto once on a square so portrait wins its tie.
        var orientation = paper.Orientation == OrientationMode.Landscape ? OrientationMode.Landscape : OrientationMode.Portrait;
        decimal w = orientation == OrientationMode.Portrait ? Math.Min(paper.WidthInches, paper.HeightInches) : Math.Max(paper.WidthInches, paper.HeightInches);
        decimal h = orientation == OrientationMode.Portrait ? Math.Max(paper.WidthInches, paper.HeightInches) : Math.Min(paper.WidthInches, paper.HeightInches);
        return LayoutEngine.Calculate(new((w - paper.MarginInches * 2) * 2, (h - paper.MarginInches * 2) * 2), paper with { Orientation = orientation });
    }
}
