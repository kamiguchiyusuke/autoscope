using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace ArmadaAnalyzer.Desktop;

public partial class MainWindow
{
    private async void Report_Click(object sender, RoutedEventArgs e)
    {
        if(busy)return;
        if(analysis is not {Count: >0}){StatusText.Text="データを分析してからレポートを保存してください。";return;}
        var dialog=new SaveFileDialog{Filter="PDFレポート|*.pdf",DefaultExt=".pdf",FileName=$"AutoScope-{DateTime.Now:yyyyMMdd-HHmm}.pdf"};
        if(dialog.ShowDialog(this)!=true)return;
        await RunJob("レポートを作成中（推定の完了を待っています）",async(token,progress)=>
        {
            await ExportReportAsync(dialog.FileName,token);
            return "PDFレポートを保存しました: "+dialog.FileName;
        });
    }

    internal async Task ExportReportAsync(string path,CancellationToken token=default)
    {
        var result=analysis;var source=snapshot;
        if(result is not {Count: >0} || source==null)throw new InvalidOperationException("分析結果がありません。");
        await Task.WhenAll(MetricEstimateTask,SlippageTask).WaitAsync(token);
        if(!ReferenceEquals(result,analysis)||!ReferenceEquals(source,snapshot))throw new InvalidOperationException("分析結果が変更されました。もう一度出力してください。");
        var metrics=new (string,string)[]{
            ("総損益 / 収束総損益 / 総損益収束率",$"{NetValue.Text} / {NetEstimateValue.Text} / {ConvergenceValue.Text}"),
            ("PF / 収束PF",$"{PfValue.Text} / {PfEstimateValue.Text}"),
            ("初資比DD / 収束初資比DD",$"{InitialDdPercentValue.Text} / {InitialDdEstimateValue.Text}"),
            ("最大残高DD / 収束最大残高DD",$"{DdPercentValue.Text} / {DdEstimateValue.Text}"),
            ("勝率 / 収束勝率 / 損益分岐勝率",$"{WinValue.Text} / {WinRangeValue.Text} / {BreakEvenValue.Text}"),
            ("総取引回数 / 総セット数",CountValue.Text),
            ("統計的信頼スコア / 期間的信頼スコア",$"{TrustScoreValue.Text} / {PeriodTrustValue.Text}"),
            ("平均獲得pips / スリップ耐久pips",$"{AveragePipsValue.Text} / {SlippageValue.Text}")};
        var trades=new (string,string)[]{("総利益",GrossValue.Text),("総損失",LossValue.Text),("平均損益",ExpectedValue.Text),
            ("勝ち数",result.Wins.ToString("N0")),("負け数",result.Losses.ToString("N0")),("引き分け数",result.Zeros.ToString("N0")),
            ("平均利益",AverageProfitValue.Text),("平均損失",AverageLossValue.Text),("最大連勝 / 連敗",StreakValue.Text),
            ("平均連勝数 / 理論平均",AverageWinsValue.Text),("平均連敗数 / 理論平均",AverageLossesValue.Text),
            ("総獲得pips",TotalPipsValue.Text),("最大獲得pips",MaxPipsValue.Text),("最小獲得pips",MinPipsValue.Text),
            ("勝ち取引の平均pips",WinPipsValue.Text),("負け取引の平均pips",LossPipsValue.Text),
            ("総保有時間",TotalHoldingValue.Text),("最大保有時間",MaxHoldingValue.Text),("保有時間平均",AverageHoldingValue.Text),
            ("最大同時保有ポジション",MaxPositionsValue.Text),("ロング最大同時保有ポジション",LongPositionsValue.Text),("ショート最大同時保有ポジション",ShortPositionsValue.Text)};
        string title=AnalysisTitle.Text;
        var dd=DrawdownGrid.Rows?.ToArray()??[];
        var monthly=await Task.Run(()=>DashboardAggregation.Months(source.Points,token),token);
        var metadata=await Task.Run(()=>workspace.LoadReportMetadata(appliedFilter!,token),token);
        var period=await Task.Run(()=>workspace.DisplayPeriod(appliedFilter!,token),token);
        var scatter=holdingKey==source.Key && holding!=null?holding:await Task.Run(()=>workspace.LoadHoldingScatter(appliedFilter!,token:token),token);
        var summary=ReportHeaderSummary.Create(datasets.Where(x=>appliedFilter!.DatasetIds.Contains(x.Data.Id)).Select(x=>x.Data.Symbol),metadata);
        string[] chartLabels=[PeriodTrustValue.Text,TrustScoreValue.Text,AnnualReturnValue.Text,TotalReturnValue.Text];
        var header=new (string,string)[]{
            ("通貨",summary.Currency),
            ("期間",period==null?"不明":$"{new DateTime(period.Start):yyyy/MM/dd}〜{new DateTime(period.End):yyyy/MM/dd}"),
            ("初期資本（分析）",NumberDisplay.GroupedTwoDecimal((decimal)initialCapital/Amount.Scale)),
            ("MT4 / MT5",summary.Platform),("スプレッド",summary.Spread),("モデリング / 履歴品質",summary.Quality),
            ("不利ポジション設定","0")};
        // Render on an isolated STA: no UI navigation, theme changes or live-chart capture.
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>{try{PdfReport.Write(path,title,result,source,metrics,trades,dd,monthly,header,chartLabels,scatter,token);completion.SetResult();}catch(Exception ex){completion.SetException(ex);}}){IsBackground=true};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();await completion.Task;
    }
}

internal static class PdfReport
{
    const double W=794,H=1123;
    static readonly Typeface Font=new("Yu Mincho");
    static Brush ColorBrush(byte r,byte g,byte b){var brush=new SolidColorBrush(Color.FromRgb(r,g,b));brush.Freeze();return brush;}
    static readonly Brush Ink=ColorBrush(35,39,45),Red=ColorBrush(190,25,42),Gain=ColorBrush(31,111,112);
    static void Text(DrawingContext dc,string text,double x,double y,double width,double size=12,Brush? color=null,TextAlignment align=TextAlignment.Left)
    {
        size*=1.1;
        var f=new FormattedText(text,CultureInfo.GetCultureInfo("ja-JP"),FlowDirection.LeftToRight,Font,size,color??Ink,1){MaxTextWidth=width,MaxTextHeight=Math.Max(36,size*2),Trimming=TextTrimming.CharacterEllipsis,TextAlignment=align};
        dc.DrawText(f,new Point(x,y));
    }
    static void Rule(DrawingContext d,double y)=>d.DrawLine(new Pen(Brushes.LightGray,.7),new Point(38,y),new Point(W-38,y));
    static double Pair(DrawingContext d,string value,string label,double x,double y,double width,double size,Brush brush,double labelSize=9)
    {
        FormattedText Make(string text,double fontSize,Brush color)=>new(text,CultureInfo.GetCultureInfo("ja-JP"),FlowDirection.LeftToRight,Font,fontSize,color,1){MaxTextWidth=width,TextAlignment=TextAlignment.Center};
        var number=Make(value,size*1.1,brush);var caption=Make(label,labelSize*1.1,Brushes.DimGray);
        var a=number.BuildGeometry(new Point()).Bounds;var b=caption.BuildGeometry(new Point()).Bounds;
        d.DrawText(number,new Point(x,y-a.Top));
        d.DrawText(caption,new Point(x,y+a.Height-b.Top));
        return a.Height+b.Height;
    }
    static byte[] Page(string heading,int page,Action<DrawingContext> content)
    {
        var v=new DrawingVisual();using(var d=v.RenderOpen())
        {
            d.DrawRectangle(Brushes.White,null,new Rect(0,0,W,H));
            if(page==1){
                Text(d,"Strategy Tester Report",38,28,718,24,align:TextAlignment.Center);
                Text(d,"Auto Scope",38,65,718,19,align:TextAlignment.Center);
                Text(d,heading,38,103,718,12,align:TextAlignment.Center);
            }else{
                Text(d,"Auto Scope",38,25,500,16);Text(d,"分析レポート",650,29,105,10);
                d.DrawLine(new Pen(Red,.7),new Point(38,50),new Point(W-38,50));Text(d,heading,38,64,718,13);
            }
            content(d);Rule(d,1080);
            Text(d,"Auto Scope / ローカル分析結果",38,1090,600,10);Text(d,page.ToString(),724,1090,30,10);
        }
        var bitmap=new RenderTargetBitmap(1588,2246,192,192,PixelFormats.Pbgra32);bitmap.Render(v);
        var encoder=new JpegBitmapEncoder{QualityLevel=96};encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output=new MemoryStream();encoder.Save(output);return output.ToArray();
    }
    static double Cards(DrawingContext d,(string Label,string Value)[] values,double top,int columns,double rowStep=0)
    {
        double cell=718.0/columns,y=top;
        foreach(var row in values.Chunk(columns))
        {
            double rowHeight=0;
            for(int i=0;i<row.Length;i++){
                double x=38+i*cell,size=columns==2?16:17;
                string value=row[i].Value.Replace("\r","").Replace("\n"," ");
                var measured=new FormattedText(value,CultureInfo.GetCultureInfo("ja-JP"),FlowDirection.LeftToRight,Font,size*1.1,Ink,1);
                if(measured.Width>cell-12)size=Math.Max(9,size*(cell-12)/measured.Width);
                rowHeight=Math.Max(rowHeight,Pair(d,value,row[i].Label,x+6,y,cell-12,size,value.StartsWith('-')?Red:Ink,11));
            }
            y+=Math.Max(rowHeight+6,rowStep);
        }
        return y;
    }
    static void Chart(DrawingContext d,long[] values,SeriesPoint[] dates,ChartDrawdown[] bands,double top,double height,bool underwater,string[]? labels=null)
    {
        // -1 represents the initial capital before the first trade, at the left edge.
        bands=bands.Select(b=>b.Start<0?b with{Start=0}:b).ToArray();
        double left=100,width=650;long lo=Math.Min(0,values.Min()),hi=Math.Max(0,values.Max());
        double span=(double)hi-lo;if(span==0)span=1;
        double X(int i)=>left+ChartSampling.TimeFraction(dates,0,dates.Length,i)*width;
        double Y(long v)=>top+height-((double)v-lo)/span*height;
        if(underwater) { lo=-hi; hi=0; span=Math.Max(1,-(double)lo); }
        for(int i=0;i<=4;i++){double y=top+i*height/4;d.DrawLine(new Pen(Brushes.LightGray,.5),new Point(left,y),new Point(left+width,y));Text(d,NumberDisplay.GroupedTwoDecimal(((decimal)hi-((decimal)hi-lo)*i/4)/Amount.Scale),38,y-6,58,9);}
        for(int i=1;i<5;i++){double x=left+width*i/5;d.DrawLine(new Pen(Brushes.LightGray,.5),new Point(x,top),new Point(x,top+height));long tick=dates[0].Ticks+(long)((dates[^1].Ticks-dates[0].Ticks)*(i/5.0));Text(d,new DateTime(tick).ToString("yyyy/MM"),x-22,top+height+5,60,9);}
        var orderedBands=bands.OrderBy(b=>b.Start).ToArray();
        var headingBoxes=ChartHeadingLayout.Arrange(orderedBands.Select(b=>(X(b.Start)+X(b.End))/2-35).ToArray(),orderedBands.Select(_=>85.0).ToArray(),left,width);
        for(int bandIndex=0;bandIndex<orderedBands.Length;bandIndex++)
        {
            var b=orderedBands[bandIndex];
            double x=X(b.Start),end=X(b.End);d.DrawRectangle(new SolidColorBrush(Color.FromArgb(24,220,23,35)),null,new Rect(x,top,Math.Max(1,end-x),height));
            var days=(dates[b.End].Ticks-dates[b.Start].Ticks)/(double)TimeSpan.TicksPerDay;
            double hx=headingBoxes[bandIndex].Left;
            Text(d,(b.Recovered?"":"未回復 ")+NumberDisplay.TwoDecimal(days)+"日",hx,top-17,85,8,align:TextAlignment.Center);
            d.DrawLine(new Pen(Brushes.LightGray,.5),new Point(hx+42,top-3),new Point((x+end)/2,top));
        }
        var points=ChartSampling.SampleTimed(values,dates,0,values.Length,1300);var g=new StreamGeometry();
        using(var c=g.Open())for(int i=0;i<points.Length;i++){var p=points[i];var at=new Point(X(p.Index),Y(underwater?-p.Value:p.Value));if(i==0)c.BeginFigure(at,false,false);else {c.LineTo(new Point(at.X,Y(underwater?-points[i-1].Value:points[i-1].Value)),true,false);c.LineTo(at,true,false);}}
        g.Freeze();d.DrawGeometry(null,new Pen(underwater?Red:Gain,1),g);
        if(labels!=null){
            var occupied=new List<Rect>{new(left+6,top+6,115,85),new(left+width-100,top+height-78,100,78)};
            for(int i=0;i<4;i++){
                double x=i<2?left+6:left+width-94,y=i<2?top+6+i*43:top+height-72+(i-2)*36,labelWidth=i<2?105:90;
                Brush brush=Ink;if(i<2 && double.TryParse(labels[i].TrimEnd('%'),out double score))brush=score>=85?ColorBrush(49,103,169):score>=75?ColorBrush(150,98,14):Red;
                Pair(d,labels[i],new[]{"期間的信頼スコア","統計的信頼スコア","平均年利","総増益率"}[i],x,y,labelWidth,i<2?18:16,brush);
            }
            foreach(var band in bands.OrderBy(b=>b.Start)){
                const double bw=77,bh=29;double preferred=Math.Clamp((X(band.Start)+X(band.End))/2-bw/2,left,left+width-bw);
                Rect best=Rect.Empty;double bestCost=double.MaxValue;
                for(int slot=-1;slot<=(int)((width-bw)/8);slot++){
                    double x=slot<0?preferred:left+slot*8;double high=double.PositiveInfinity,low=double.NegativeInfinity;
                    for(int j=1;j<points.Length;j++)if(X(points[j].Index)>=x && X(points[j-1].Index)<=x+bw){double a=Y(points[j-1].Value),b=Y(points[j].Value);high=Math.Min(high,Math.Min(a,b));low=Math.Max(low,Math.Max(a,b));}
                    foreach(double y in new[]{high-bh-5,low+5}){
                        if(!double.IsFinite(y)||y<top||y+bh>top+height)continue;var box=new Rect(x,y,bw,bh);if(occupied.Any(r=>r.IntersectsWith(box)))continue;
                        double cost=Math.Abs(x-preferred)*4+Math.Abs(y-Y(values[band.Trough]));if(cost<bestCost){best=box;bestCost=cost;}
                    }
                }
                if(best.IsEmpty)continue;occupied.Add(best);
                double anchorX=X(band.Trough),anchorY=Y(values[band.Trough]);
                d.DrawLine(new Pen(Brushes.Gray,.4),new Point(anchorX,anchorY),new Point(Math.Clamp(anchorX,best.Left,best.Right),best.Top>anchorY?best.Top:best.Bottom));
                Text(d,band.InitialPercent is decimal p?NumberDisplay.TwoDecimal(p)+"%":"算出不可",best.X,best.Y,bw,13,Ink,TextAlignment.Center);
                Text(d,"初比DD",best.X,best.Y+16,bw,10,Ink,TextAlignment.Center);
            }
        }
        Text(d,new DateTime(dates[0].Ticks).ToString("yyyy/MM/dd"),left,top+height+5,100,9);
        Text(d,new DateTime(dates[^1].Ticks).ToString("yyyy/MM/dd"),left+width-65,top+height+5,80,9);
    }
    static void HoldingPips(DrawingContext d,HoldingScatterResult scatter)
    {
        var points=scatter.Points.Where(p=>p.Pips!=null).ToArray();
        Text(d,$"全{scatter.Total:N0}取引 / pips換算可能 {scatter.PipCount:N0}取引 / 表示 {points.Length:N0}座標",38,105,718,11);
        if(points.Length==0){Text(d,"pips単位を確認できる取引がありません。",38,160,718,13);return;}
        const double left=105,top=165,width=640,height=285;
        double xmax=Math.Max(1.0/60,points.Max(p=>(double)p.Duration/TimeSpan.TicksPerHour));
        double min=Math.Min(0,(double)points.Min(p=>p.Pips!.Value)),max=Math.Max(0,(double)points.Max(p=>p.Pips!.Value));
        if(min==max)max=min+1;
        double X(HoldingPoint p)=>left+(double)p.Duration/TimeSpan.TicksPerHour/xmax*width;
        double Y(double value)=>top+(max-value)/(max-min)*height;
        Text(d,"pips",38,top-25,60,11);
        for(int i=0;i<=5;i++){
            double x=left+width*i/5,y=top+height*i/5,hours=xmax*i/5;
            d.DrawLine(new Pen(Brushes.LightGray,.5),new(left,y),new(left+width,y));
            d.DrawLine(new Pen(Brushes.LightGray,.5),new(x,top),new(x,top+height));
            Text(d,NumberDisplay.GroupedTwoDecimal((decimal)(max-(max-min)*i/5)),38,y-7,62,10);
            Text(d,hours<1?$"{hours*60:0.##}分":$"{hours:0.##}時間",x-35,top+height+8,70,10,align:TextAlignment.Center);
        }
        d.DrawLine(new Pen(Brushes.Gray,.8),new(left,Y(0)),new(left+width,Y(0)));
        var positive=new StreamGeometry();var negative=new StreamGeometry();
        using(var gain=positive.Open())using(var loss=negative.Open())foreach(var p in points){
            double x=X(p),y=Y((double)p.Pips!.Value);var g=p.Pips>=0?gain:loss;
            g.BeginFigure(new(x,y-2),true,true);g.LineTo(new(x+2,y),false,false);g.LineTo(new(x,y+2),false,false);g.LineTo(new(x-2,y),false,false);
        }
        positive.Freeze();negative.Freeze();d.DrawGeometry(Gain,null,positive);d.DrawGeometry(Red,null,negative);
        Text(d,"保有時間",left,top+height+35,width,11,align:TextAlignment.Center);
    }
    internal static void Write(string path,string title,AnalysisResult a,Snapshot s,(string,string)[] metrics,(string,string)[] trades,DrawdownRow[] dd,MonthlyRow[] months,(string,string)[] header,string[] chartLabels,HoldingScatterResult scatter,CancellationToken token)
    {
        var images=new List<byte[]>();token.ThrowIfCancellationRequested();
        images.Add(Page(title,1,d=>{
            for(int i=0;i<header.Length;i++){double x=38+i%3*(718.0/3),y=155+i/3*27,labelWidth=i%3==1?65:110;Text(d,header[i].Item1,x,y,labelWidth,9,Brushes.DimGray);Text(d,header[i].Item2,x+labelWidth,y,235-labelWidth,10);}
            double headerEnd=155+Math.Ceiling(header.Length/3.0)*27+12;
            Rule(d,headerEnd);Text(d,"総損益 / 残高ドローダウン（全分析期間）",38,headerEnd+11,718,11);
            Chart(d,a.Equity,s.Points,a.ChartDrawdowns,headerEnd+51,190,false,chartLabels);
            Chart(d,a.Drawdowns,s.Points,[],headerEnd+273,50,true);
            double top=headerEnd+351;
            // Group related measures; vary row widths without leaving an orphan card.
            (string,string)[][] rows=[
                [metrics[0],metrics[1]], // Net result and profit factor.
                [trades[0],trades[1],trades[2]], // Gross profit, loss and expectancy.
                [trades[6],trades[7]], // Average winning and losing trade.
                [metrics[4],metrics[5]], // Win rate and trade/set counts.
                [trades[3],trades[4],trades[5]], // Wins, losses and flat trades.
                [metrics[2],metrics[3],metrics[6]], // Drawdown and confidence.
                [trades[8],trades[9],trades[10]], // Winning/losing streaks.
                [metrics[7],trades[11]], // Average/slippage and total pips.
                [trades[12],trades[13],trades[14],trades[15]], // Pips distribution.
                [trades[19],trades[20],trades[21]], // Concurrent exposure.
                [trades[16],trades[17],trades[18]] // Holding durations.
            ];
            double step=(1062-top)/rows.Length,cardTop=top;
            foreach(var row in rows)cardTop=Cards(d,row,cardTop,row.Length,step);
        }));token.ThrowIfCancellationRequested();
        images.Add(Page("保有時間とpips",2,d=>{
            HoldingPips(d,scatter);
            Text(d,"DDと回復期間",38,530,718,13);
            double y=570;
            foreach(var r in dd){
                Rule(d,y-6);Text(d,r.Name.Replace("\n"," / "),38,y,240,11);
                Text(d,$"最大 {r.Maximum} / 回数 {r.Count}",290,y,460,11);
                Text(d,$"平均回復 {r.Average} / 最長 {r.Longest}",38,y+22,710,10);
                Text(d,$"回復済 {r.Recovered} / 未回復区間 {r.Ongoing.Replace("\n"," ")}",38,y+40,710,9);
                y+=64;
            }
        }));
        token.ThrowIfCancellationRequested();
        foreach(var group in months.Chunk(26))
        {
            token.ThrowIfCancellationRequested();int page=images.Count+1;
            images.Add(Page("月別損益（決済時刻で集計）",page,d=>{
                const double cw=51.28;string[] labels=["年",..Enumerable.Range(1,12).Select(i=>i+"月"),"合計"];
                for(int i=0;i<14;i++)Text(d,labels[i],38+i*cw,136,cw,10);
                double y=164;
                foreach(var row in group){Rule(d,y-5);Text(d,row.Year.ToString(),38,y,cw,10);
                    for(int i=0;i<13;i++){long value=i==12?row.Total:row.Profit[i];Text(d,i<12&&row.Count[i]==0?"—":NumberDisplay.GroupedTwoDecimal((decimal)value/Amount.Scale),38+(i+1)*cw,y,cw-3,8,value<0?Red:Ink);}y+=33;}
            }));
        }
        SavePdf(path,images,token);
    }
    // Small, self-contained image-PDF writer. No printer driver, network or external runtime.
    static void SavePdf(string path,List<byte[]> pages,CancellationToken token)
    {
        string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{
            using(var f=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){
                void Put(string text){var b=Encoding.ASCII.GetBytes(text);f.Write(b);}
                var offsets=new List<long>{0};void Obj(int id,string value){offsets.Add(f.Position);Put($"{id} 0 obj\n{value}\nendobj\n");}
                Put("%PDF-1.4\n");Obj(1,"<< /Type /Catalog /Pages 2 0 R >>");Obj(2,$"<< /Type /Pages /Count {pages.Count} /Kids [ {string.Join(" ",Enumerable.Range(0,pages.Count).Select(i=>$"{3+i*3} 0 R"))} ] >>");
                for(int i=0;i<pages.Count;i++){
                    token.ThrowIfCancellationRequested();int id=3+i*3;
                    Obj(id,$"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595.28 841.89] /Resources << /XObject << /Im {id+1} 0 R >> >> /Contents {id+2} 0 R >>");
                    offsets.Add(f.Position);Put($"{id+1} 0 obj\n<< /Type /XObject /Subtype /Image /Width 1588 /Height 2246 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {pages[i].Length} >>\nstream\n");f.Write(pages[i]);Put("\nendstream\nendobj\n");
                    string draw="q 595.28 0 0 841.89 0 0 cm /Im Do Q\n";Obj(id+2,$"<< /Length {Encoding.ASCII.GetByteCount(draw)} >>\nstream\n{draw}endstream");
                }
                long xref=f.Position;Put($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");foreach(long offset in offsets.Skip(1))Put(offset.ToString("D10",CultureInfo.InvariantCulture)+" 00000 n \n");Put($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            }
            token.ThrowIfCancellationRequested();File.Move(temp,path,true);
        }finally{if(File.Exists(temp))File.Delete(temp);}
    }
}
