using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MorphocyteRouter;

// Small vector badges do not depend on Windows emoji flag support. The country
// comes from the provider's label, never from a guessed server IP location.
internal static class CountryBadge
{
    internal static FrameworkElement Create(string? code)
    {
        var group=new DrawingGroup { ClipGeometry=new EllipseGeometry(new Point(15,10),10,10) };
        void Rectangle(double x,double y,double width,double height,string color) => group.Children.Add(new GeometryDrawing(
            new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)),null,new RectangleGeometry(new Rect(x,y,width,height))));
        void Stripes(bool vertical,params string[] colors)
        {
            for(var i=0;i<colors.Length;i++) Rectangle(vertical?30d*i/colors.Length:0,vertical?0:20d*i/colors.Length,
                vertical?30d/colors.Length:30,vertical?20:20d/colors.Length,colors[i]);
        }
        void Cross(string background,string outer,string? inner=null)
        {
            Rectangle(0,0,30,20,background); Rectangle(9,0,4,20,outer); Rectangle(0,8,30,4,outer);
            if(inner is not null) { Rectangle(10,0,2,20,inner); Rectangle(0,9,30,2,inner); }
        }
        switch(code)
        {
            case "AUTO":
                Rectangle(0,0,30,20,"#278DE2");
                var automaticStar=new StreamGeometry();
                using(var points=automaticStar.Open())
                    for(var p=0;p<10;p++)
                    {
                        var a=p*Math.PI/5-Math.PI/2; var r=p%2==0?6.7:3;
                        var point=new Point(15+Math.Cos(a)*r,10+Math.Sin(a)*r);
                        if(p==0) points.BeginFigure(point,true,true); else points.LineTo(point,true,false);
                    }
                group.Children.Add(new GeometryDrawing(Brushes.White,null,automaticStar));
                break;
            case "DE": Stripes(false,"#111111","#DD0000","#FFCE00"); break;
            case "FI": Cross("#FFFFFF","#003580"); break;
            case "SE": Cross("#006AA7","#FECC00"); break;
            case "PL": Stripes(false,"#FFFFFF","#DC143C"); break;
            case "NL": Stripes(false,"#AE1C28","#FFFFFF","#21468B"); break;
            case "LT": Stripes(false,"#FDB913","#006A44","#C1272D"); break;
            case "EE": Stripes(false,"#4891D9","#111111","#FFFFFF"); break;
            case "BG": Stripes(false,"#FFFFFF","#00966E","#D62612"); break;
            case "RU": Stripes(false,"#FFFFFF","#0039A6","#D52B1E"); break;
            case "UA": Stripes(false,"#0057B7","#FFDD00"); break;
            case "AT": Stripes(false,"#ED2939","#FFFFFF","#ED2939"); break;
            case "FR": Stripes(true,"#0055A4","#FFFFFF","#EF4135"); break;
            case "IT": Stripes(true,"#009246","#FFFFFF","#CE2B37"); break;
            case "IE": Stripes(true,"#169B62","#FFFFFF","#FF883E"); break;
            case "BE": Stripes(true,"#111111","#FDDA24","#EF3340"); break;
            case "DK": Cross("#C8102E","#FFFFFF"); break;
            case "NO": Cross("#BA0C2F","#FFFFFF","#00205B"); break;
            case "IS": Cross("#02529C","#FFFFFF","#DC1E35"); break;
            case "CH": Rectangle(0,0,30,20,"#DA291C"); Rectangle(13,4,4,12,"#FFFFFF"); Rectangle(9,8,12,4,"#FFFFFF"); break;
            case "CZ":
                Stripes(false,"#FFFFFF","#D7141A");
                group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(17,69,126)),null,Geometry.Parse("M0,0 L15,10 L0,20 Z"))); break;
            case "JP":
                Rectangle(0,0,30,20,"#FFFFFF"); group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(188,0,45)),null,new EllipseGeometry(new Point(15,10),6,6))); break;
            case "EU":
                Rectangle(0,0,30,20,"#003399");
                for(var i=0;i<12;i++)
                {
                    var angle=i*Math.PI/6; var x=15+6*Math.Sin(angle); var y=10-6*Math.Cos(angle);
                    var star=new StreamGeometry();
                    using(var points=star.Open())
                    {
                        for(var p=0;p<10;p++)
                        {
                            var a=p*Math.PI/5-Math.PI/2; var r=p%2==0?1.1:.45; var point=new Point(x+Math.Cos(a)*r,y+Math.Sin(a)*r);
                            if(p==0) points.BeginFigure(point,true,true); else points.LineTo(point,true,false);
                        }
                    }
                    group.Children.Add(new GeometryDrawing(Brushes.Gold,null,star));
                }
                break;
            default:
                var fallback=new Border { Width=26,Height=26,CornerRadius=new CornerRadius(13),VerticalAlignment=VerticalAlignment.Center };
                fallback.SetResourceReference(Border.BackgroundProperty,"ThemeSurfaceSelected");
                if(code is not null)
                {
                    var text=new TextBlock { Text=code,FontSize=10,FontWeight=FontWeights.SemiBold,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center };
                    text.SetResourceReference(TextBlock.ForegroundProperty,"ThemeMuted"); fallback.Child=text;
                }
                else
                {
                    var globe=new System.Windows.Shapes.Path { Width=18,Height=18,Stretch=Stretch.Uniform,StrokeThickness=1,
                        Data=Geometry.Parse("M 9,0 A 9,9 0 1 1 9,18 A 9,9 0 1 1 9,0 M 0,9 L 18,9 M 9,0 C 3,4 3,14 9,18 M 9,0 C 15,4 15,14 9,18"),
                        HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center };
                    globe.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,"ThemeMuted"); fallback.Child=globe;
                }
                return fallback;
        }
        group.Freeze();
        return new Image { Source=new DrawingImage(group),Width=26,Height=26,VerticalAlignment=VerticalAlignment.Center,ToolTip=code };
    }
}
