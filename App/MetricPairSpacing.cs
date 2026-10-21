using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
namespace ArmadaAnalyzer.Desktop;
public partial class MainWindow
{
    private void TightenChartMetricPairs()
    {
        foreach(var value in new[]{PeriodTrustValue,TrustScoreValue,AnnualReturnValue,TotalReturnValue})
        {
            if(value.Parent is not StackPanel panel)continue;
            int index=panel.Children.IndexOf(value);
            if(index+1>=panel.Children.Count||panel.Children[index+1] is not TextBlock label)continue;
            void Update()
            {
                FormattedText Text(TextBlock t)=>new(t.Text,CultureInfo.GetCultureInfo("ja-JP"),FlowDirection.LeftToRight,new Typeface(t.FontFamily,t.FontStyle,t.FontWeight,t.FontStretch),t.FontSize,Brushes.Black,1);
                var v=Text(value);var l=Text(label);var a=v.BuildGeometry(new Point()).Bounds;var b=l.BuildGeometry(new Point()).Bounds;
                if(a.IsEmpty||b.IsEmpty)return;
                label.Margin=new Thickness(0,a.Bottom-v.Height-b.Top,0,0);
            }
            value.Loaded+=(_,_)=>Update();
            DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty,typeof(TextBlock)).AddValueChanged(value,(_,_)=>Update());
        }
    }
}
