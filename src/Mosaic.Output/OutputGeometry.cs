using Mosaic.Core;

namespace Mosaic.Output;

// Shared by the PDF and WPF adapters: neither adapter calculates a second layout.
public sealed record PageDrawing(RectInches Clip, RectInches Image);
public static class OutputGeometry
{
    public static PageDrawing Describe(RenderSnapshot snapshot, PageTile tile, int imageWidth, int imageHeight)
    {
        // Raster caches may round an edge by a pixel. Geometry always follows the immutable source ratio.
        bool swapped = Math.Abs(snapshot.State.Composition.Rotation % 180) == 90;
        int sourceWidth = swapped ? snapshot.Source.Height : snapshot.Source.Width;
        int sourceHeight = swapped ? snapshot.Source.Width : snapshot.Source.Height;
        var place = LayoutEngine.ImagePlacement(snapshot.Layout.Artwork, sourceWidth, sourceHeight, snapshot.State.Composition);
        return new(tile.PaperBounds, new(
            tile.PaperBounds.X - tile.ArtworkBounds.X + place.X,
            tile.PaperBounds.Y - tile.ArtworkBounds.Y + place.Y,
            place.Width, place.Height));
    }
}
