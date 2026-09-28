using System.Numerics;
using Beans.Windows.Rebuild.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
namespace Beans.Windows.Rebuild.Pages.Anime;

internal static class AnimeUi
{
    private static ResourceDictionary? _theme;
    public static ResourceDictionary Theme => _theme ??= new() { Source = new Uri("ms-appx:///Styles/Anime.xaml") };
    public static SolidColorBrush Ink => (SolidColorBrush)Theme["AnimeInkBrush"];
    public static SolidColorBrush Sky => (SolidColorBrush)Theme["AnimeSkyBrush"];
    public static SolidColorBrush Night => (SolidColorBrush)Theme["AnimeNightBrush"];
    public static SolidColorBrush Mint => (SolidColorBrush)Theme["AnimeMintBrush"];
    public static SolidColorBrush Brush(byte r, byte g, byte b, byte alpha = 255) => new(ColorHelper.FromArgb(alpha, r, g, b));
    public static TextBlock Text(string text, double size = 14, bool light = false) => new()
    {
        Text = text, FontSize = size, Foreground = light ? Brush(239,245,255) : Ink,
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"),
        FontWeight = size >= 18 ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center
    };
    public static TextBlock Muted(string text, double size = 12, bool dark = false)
    { var t = Text(text,size,dark); t.Foreground = dark ? Brush(174,193,218) : Brush(112,145,182); return t; }
    public static StackPanel Stack(params UIElement[] children) { var p = new StackPanel { Spacing = 16 }; foreach (var c in children) p.Children.Add(c); return p; }
    public static StackPanel Inline(params UIElement[] children) { var p = Stack(children); p.Orientation = Orientation.Horizontal; p.Spacing = 10; p.VerticalAlignment = VerticalAlignment.Center; return p; }
    public static FontIcon Icon(string glyph, double size = 18) => new() { Glyph = glyph, FontSize = size, FontFamily = new FontFamily("Segoe Fluent Icons") };
    public static Button Button(string text, Action action, string kind = "ghost", string? glyph = null)
    {
        var style = kind switch { "primary"=>"AnimePrimaryButtonStyle", "dark"=>"AnimeDarkButtonStyle", "violet"=>"AnimeVioletButtonStyle", _=>"AnimeButtonStyle" };
        var b = new Button { Content = text, Style = (Style)Theme[style] };
        if (glyph is not null)
        {
            var icon = Icon(glyph, 15);
            var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
            label.SetBinding(TextBlock.ForegroundProperty, new Microsoft.UI.Xaml.Data.Binding { Source = b, Path = new PropertyPath("Foreground") });
            icon.SetBinding(FontIcon.ForegroundProperty, new Microsoft.UI.Xaml.Data.Binding { Source = b, Path = new PropertyPath("Foreground") });
            b.Content = Inline(icon, label);
        }
        b.Click += (_,_)=>action(); Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b,text); return b;
    }
    public static Button IconButton(string label,string glyph,Action action,bool dark=false)
    {
        var b=Button(label,action);b.Style=(Style)Theme["AnimeIconButtonStyle"];b.Content=Icon(glyph);
        if(dark)b.Foreground=Brush(225,236,252);ToolTipService.SetToolTip(b,label);return b;
    }
    public static Border SearchBox(TextBox input, Action? submit=null)
    {
        input.BorderThickness=new Thickness(0); input.CornerRadius=new CornerRadius(22);input.Background=Brush(255,255,255,0);
        input.Foreground=Ink;input.Padding=new Thickness(0,8,8,8);input.FontSize=14;input.MinHeight=38;
        input.Resources["TextControlBorderBrushFocused"]=Brush(255,255,255,0);
        input.Resources["TextControlBorderBrushPointerOver"]=Brush(255,255,255,0);
        input.Resources["TextControlBorderThemeThicknessFocused"]=new Thickness(0);
        input.Resources["TextControlBackgroundFocused"]=Brush(255,255,255,0);
        var row=new Grid {ColumnSpacing=10,ColumnDefinitions={new(){Width=GridLength.Auto},new(){Width=new GridLength(1,GridUnitType.Star)}}};
        if(submit is null){var icon=Icon("\uE721",16);icon.Foreground=Brush(113,148,192);icon.Margin=new Thickness(14,0,0,0);row.Children.Add(icon);}
        else{var search=IconButton("执行搜索","\uE721",submit);search.Margin=new Thickness(4,0,0,0);row.ColumnSpacing=2;row.Children.Add(search);}
        Grid.SetColumn(input,1);row.Children.Add(input);
        if(submit is not null)input.KeyDown+=(_,e)=>{if(e.Key==global::Windows.System.VirtualKey.Enter){e.Handled=true;submit();}};
        return new Border {Child=row,CornerRadius=new CornerRadius(24),Background=Brush(255,255,255,205),BorderBrush=Brush(154,184,215,40),BorderThickness=new Thickness(1),MinHeight=40};
    }
    public static Grid Row(params UIElement[] children)
    {
        var g=new Grid {ColumnSpacing=8};for(var i=0;i<children.Length;i++){g.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});Grid.SetColumn((FrameworkElement)children[i],i);g.Children.Add(children[i]);}return g;
    }
    public static Border Card(UIElement child,bool dark=false) => new()
    {
        Child=child,Padding=new Thickness(20),CornerRadius=new CornerRadius(12),
        Background=dark?Brush(13,29,48,190):new AcrylicBrush
        {
            TintColor=dark?ColorHelper.FromArgb(255,13,29,48):ColorHelper.FromArgb(255,242,250,255),
            TintOpacity=dark?.64:.38,TintLuminosityOpacity=dark?.25:.48,
            FallbackColor=dark?ColorHelper.FromArgb(230,13,29,48):ColorHelper.FromArgb(220,242,250,255)
        },
        BorderThickness=new Thickness(1),BorderBrush=dark?Brush(153,191,233,20):Brush(255,255,255,110)
    };
    public static Grid Poster(string uri,double height=240,bool crop=false)
    {
        var g=new Grid {Height=height,Background=Brush(139,171,202,18),CornerRadius=new CornerRadius(8)};
        var message=Muted("海报暂不可用",11);message.HorizontalAlignment=HorizontalAlignment.Center;g.Children.Add(message);
        if(Uri.TryCreate(uri,UriKind.Absolute,out var source)){var image=new Image {Source=new BitmapImage(source),Stretch=crop?Stretch.UniformToFill:Stretch.Uniform};image.ImageOpened+=(_,_)=>message.Visibility=Visibility.Collapsed;g.Children.Add(image);}return g;
    }
    public static ScrollViewer Scroll(UIElement content)=>new(){Content=content,HorizontalContentAlignment=HorizontalAlignment.Stretch,VerticalContentAlignment=VerticalAlignment.Stretch,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,HorizontalScrollMode=ScrollMode.Disabled,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
    public static string TypeLabel(AnimeSongType t)=>t switch {AnimeSongType.Opening=>"OP",AnimeSongType.Ending=>"ED",AnimeSongType.Insert=>"插曲",AnimeSongType.Character=>"角色曲",_=>"相关歌曲"};
    public static Border SectionTitle(string text,bool dark=false)
    {
        var label=Text(text,18,dark);var icon=Icon("\uE8D6",18);icon.Foreground=dark?Brush(239,245,255):Ink;return new Border {Child=Inline(icon,label)};
    }
    public static Grid SubjectCards(IEnumerable<AnimeSubject> subjects,Action<AnimeSubject> open,double posterHeight=210,double minimumWidth=174, bool landscape=false, Action<AnimeSubject>? favorite=null, Func<AnimeSubject,string>? actionLabel=null, Func<AnimeSubject,string>? caption=null, Func<AnimeSubject,string>? displayTitle=null)
    {
        var g=new Grid {ColumnSpacing=16,RowSpacing=18};
        var cards=subjects.Select(s=>
        {
            var poster=Poster(s.PosterUri,posterHeight,landscape);
            var title=Text(displayTitle?.Invoke(s) ?? s.Title,14);title.MaxLines=1;title.TextTrimming=TextTrimming.CharacterEllipsis;
            var jp=Muted(s.JapaneseTitle,11);jp.MaxLines=1;jp.TextTrimming=TextTrimming.CharacterEllipsis;
            var info=Stack(title,jp,Muted(caption?.Invoke(s) ?? $"{(s.Year>0?s.Year.ToString():"年份待定")} · {string.Join(" / ",s.Genres.Take(3))}",11));info.Spacing=6;info.Margin=new Thickness(10,10,10,8);
            var enter=Button(actionLabel?.Invoke(s) ?? "查看主题曲",()=>open(s),landscape?"ghost":"primary",landscape?null:"\uE8D6");enter.MinHeight=28;enter.Padding=new Thickness(10,4,10,4);enter.FontSize=11;
            if(landscape)enter.Background=Brush(97,168,249,28);
            var actions=Inline(enter);actions.Spacing=4;actions.Margin=new Thickness(8,0,8,10);
            if(favorite is not null){var heart=IconButton("收藏番剧","\uEB51",()=>favorite(s));heart.Width=30;heart.Height=28;heart.MinHeight=28;actions.Children.Add(heart);}
            var content=Stack(poster,info,actions);content.Spacing=0;
            var card=Card(content);card.Padding=new Thickness(0);card.Tapped+=(_,e)=>{if(e.OriginalSource is DependencyObject source){for(var node=source;node is not null;node=VisualTreeHelper.GetParent(node)){if(node is Button)return;if(ReferenceEquals(node,card))break;}}open(s);};return card;
        }).ToArray();
        void Layout(double width)
        {
            var available=Math.Max(1,(int)((Math.Max(minimumWidth,width)+16)/(minimumWidth+16)));
            var count=landscape?Math.Min(Math.Max(1,cards.Length),available):Math.Min(6,available);
            if(g.ColumnDefinitions.Count==count&&g.Children.Count==cards.Length)return;
            g.Children.Clear();g.RowDefinitions.Clear();g.ColumnDefinitions.Clear();
            for(var i=0;i<count;i++)g.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
            for(var i=0;i<(cards.Length+count-1)/count;i++)g.RowDefinitions.Add(new(){Height=GridLength.Auto});
            for(var i=0;i<cards.Length;i++){Grid.SetRow(cards[i],i/count);Grid.SetColumn(cards[i],i%count);g.Children.Add(cards[i]);}
        }
        g.SizeChanged+=(_,e)=>Layout(e.NewSize.Width);Layout(1200);return g;
    }
    public static FrameworkElement Chips(IEnumerable<string> values,string selected,Action<string> choose,bool dark=false)
    {
        var row=new StackPanel {Orientation=Orientation.Horizontal,Spacing=8};
        foreach(var value in values){var b=Button(value,()=>choose(value),value==selected?(dark?"violet":"primary"):"ghost");b.FontSize=12;b.Padding=new Thickness(14,4,14,4);b.MinHeight=28;
            if(value!=selected){b.Background=dark?Brush(147,175,215,14):Brush(115,166,216,15);if(dark)b.Foreground=Brush(208,221,240);}row.Children.Add(b);}
        return new ScrollViewer {Content=row,HorizontalScrollMode=ScrollMode.Enabled,HorizontalScrollBarVisibility=ScrollBarVisibility.Hidden,VerticalScrollMode=ScrollMode.Disabled,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled};
    }
}
public abstract class AnimeView:UserControl
{
    protected AnimeShell Shell {get;}
    protected CancellationTokenSource Lifetime {get;}=new();
    protected AnimeView(AnimeShell shell){Shell=shell;Unloaded+=(_,_)=>Lifetime.Cancel();}
    protected async Task Run(Func<Task> operation){try{await operation();}catch(OperationCanceledException)when(Lifetime.IsCancellationRequested){}catch(Exception){if(!Lifetime.IsCancellationRequested)Shell.Report("操作暂未完成，请重试。");}}
}
