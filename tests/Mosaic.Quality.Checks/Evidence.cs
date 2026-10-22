using System.IO;
using ImageMagick;
using ImageMagick.Drawing;

internal static class Evidence
{
    public static void Build(string root)
    {
        foreach(string fixture in new[]{"compressed-illustration-640","met-woodblock-640"})
        {
            string folder=Path.Combine(root,fixture);
            using var sheet=new MagickImage(MagickColors.White,1920,350);
            var entries=new[]{("reference.png","Known high-resolution reference"),("lanczos-matched.png","Standard: Lanczos 2×"),("cunet2-matched.png","Previous Enhanced: CUNet 2×"),("cunet-denoise1-4-matched.png","Selected Enhanced: mild cleanup 4×")};
            for(int i=0;i<entries.Length;i++)
            {
                using var image=new MagickImage(Path.Combine(folder,entries[i].Item1));
                bool scan=fixture.StartsWith("met-");
                image.Crop(new MagickGeometry((int)(image.Width*(scan?.32:.54)),(int)(image.Height*(scan?.15:.22)),480,300));image.ResetPage();sheet.Composite(image,Gravity.Northwest,i*480,40,CompositeOperator.Over);
            }
            new Drawables().FillColor(MagickColors.White).Rectangle(0,0,1920,39).Draw(sheet);
            for(int i=0;i<entries.Length;i++)new Drawables().Font("Segoe UI").FontPointSize(17).FillColor(new MagickColor("#263e42")).Text(i*480+8,25,entries[i].Item2).Draw(sheet);
            sheet.Write(Path.GetFullPath("docs/assets/enhancement-"+fixture+".png"));
        }
    }
}
