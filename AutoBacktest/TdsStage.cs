using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
namespace MT4BatchReporter;
public static class TdsStage {
const long Mod=2147483647; static readonly CultureInfo Inv=CultureInfo.InvariantCulture;
static long Hash(long h,long ms,double b,double a,double factor)=>(h*31+ms%Mod+(long)Math.Round(b*factor)*3+(long)Math.Round(a*factor)*7)%Mod;
static string Sha(string path){using var f=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();}
static long Millis(string day)=>new DateTimeOffset(DateTime.ParseExact(day,"yyyy-MM-dd",Inv,DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal)).ToUnixTimeMilliseconds();
readonly record struct Bar(long Time,double Open,double High,double Low,double Close,long Volume,int Spread,long Real=0){public void Write(BinaryWriter w){w.Write(Time);w.Write(Open);w.Write(High);w.Write(Low);w.Write(Close);w.Write(Volume);w.Write(Spread);w.Write(Real);} public static Bar Read(BinaryReader r)=>new(r.ReadInt64(),r.ReadDouble(),r.ReadDouble(),r.ReadDouble(),r.ReadDouble(),r.ReadInt64(),r.ReadInt32(),r.ReadInt64());}
static Bar Update(Bar? bar,long ms,double b,double a,double factor){long minute=ms/60000*60;int spread=(int)Math.Round((a-b)*factor);return bar is not Bar current||current.Time!=minute?new(minute,b,b,b,b,1,spread):current with{High=Math.Max(current.High,b),Low=Math.Min(current.Low,b),Close=b,Volume=current.Volume+1,Spread=spread};}
static void CheckTick(long ms,double b,double a,double f,long lower,long upper,long? last){if(ms<lower||ms>=upper||last.HasValue&&ms<last||!double.IsFinite(b)||!double.IsFinite(a)||b<=0||a<b||a>=1e9||Math.Abs(b*f-Math.Round(b*f))>.0001||Math.Abs(a*f-Math.Round(a*f))>.0001)throw new InvalidDataException("Invalid time/price/order/precision");}
static string[] Fields(string line,StreamReader reader){
 if(!line.Contains('"'))return line.Split(line.Contains('\t')?'\t':',');
 while(line.Count(c=>c=='"')%2!=0){var next=reader.ReadLine()??throw new InvalidDataException("Unclosed CSV quote");line+="\n"+next;if(line.Length>1048576)throw new InvalidDataException("CSV record too long");}
 using var parser=new Microsoft.VisualBasic.FileIO.TextFieldParser(new StringReader(line)){HasFieldsEnclosedInQuotes=true,TrimWhiteSpace=false};parser.SetDelimiters(line.Contains('\t')?"\t":",");return parser.ReadFields()??[];
}
static JsonObject Prepare(string csv,string start,string end,string output,int digits,string source,string symbol){
 if(digits<0||digits>8)throw new InvalidDataException("digits");double factor=Math.Pow(10,digits);long lower=Millis(start),upper=Millis(end)+86400000;if(lower>=upper)throw new InvalidDataException("dates");
 Directory.CreateDirectory(output);if(File.Exists(Path.Combine(output,"manifest.json")))throw new IOException("manifest exists");
 var days=new JsonArray();var m=new JsonObject{["version"]=1,["timezone"]="UTC",["source"]=source,["symbol"]=symbol,["start"]=start,["end"]=end,["digits"]=digits,["days"]=days};
 long ticks=0,bars=0,h=0,first=0,last=0,day=-1,dt=0,db=0;JsonObject? d=null;BinaryWriter? tw=null,bw=null;Bar? bar=null;
 void FlushBar(){if(bar!=null){bar.Value.Write(bw!);bars++;db++;bar=null;}}
 void FlushDay(){FlushBar();tw?.Dispose();bw?.Dispose();if(d!=null){d["ticks"]=dt;d["bars"]=db;d["ticks_sha256"]=Sha(Path.Combine(output,d["ticks_file"]!.GetValue<string>()));d["bars_sha256"]=Sha(Path.Combine(output,d["bars_file"]!.GetValue<string>()));}}
 try{using var csvReader=new StreamReader(csv);string? line;while((line=csvReader.ReadLine())!=null){
 if(string.IsNullOrWhiteSpace(line))continue;string[] c=Fields(line,csvReader);if(ticks==0 && (c[0].Contains("DATE",StringComparison.OrdinalIgnoreCase)||c[0].Contains("日時")||c.Length>1&&c[1].Contains("bid",StringComparison.OrdinalIgnoreCase)))continue;
 string stamp;double b,a;
 if(c.Length==3){stamp=c[0].Trim();b=double.Parse(c[1],Inv);a=double.Parse(c[2],Inv);}else if(c.Length is 4 or 6 or 7){stamp=c[0].Trim()+" "+c[1].Trim();b=double.Parse(c[2],Inv);a=double.Parse(c[3],Inv);}else throw new InvalidDataException("columns");
 var time=DateTime.ParseExact(stamp,"yyyy.MM.dd HH:mm:ss.FFFFFFF",Inv,DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal);if(time.Ticks%TimeSpan.TicksPerMillisecond!=0)throw new InvalidDataException("fraction");long ms=new DateTimeOffset(time).ToUnixTimeMilliseconds();CheckTick(ms,b,a,factor,lower,upper,ticks==0?null:last);
 long current=ms/86400000*86400000;
 if(day!=current){FlushDay();day=current;dt=db=0;string name=time.ToString("yyyyMMdd");d=new(){["day"]=name,["day_start"]=day,["ticks_file"]=$"ticks-{name}.bin",["bars_file"]=$"bars-{name}.bin"};days.Add(d);tw=new(new FileStream(Path.Combine(output,$"ticks-{name}.bin"),FileMode.Create,FileAccess.Write,FileShare.None,262144));bw=new(File.Create(Path.Combine(output,$"bars-{name}.bin")));}
 if(bar!=null&&bar.Value.Time!=ms/60000*60)FlushBar();bar=Update(bar,ms,b,a,factor);tw!.Write(ms);tw.Write(b);tw.Write(a);dt++;ticks++;h=Hash(h,ms,b,a,factor);if(ticks==1)first=ms;last=ms;
 }FlushDay();}finally{tw?.Dispose();bw?.Dispose();}
 if(ticks==0)throw new InvalidDataException("empty CSV");m["ticks"]=ticks;m["bars"]=bars;m["first_msc"]=first;m["last_msc"]=last;m["tick_hash"]=h;m["csv_sha256"]=Sha(csv);File.WriteAllText(Path.Combine(output,"manifest.json"),m.ToJsonString());return m;
}
static JsonObject Validate(string stage,string? start=null,string? end=null){
 var m=JsonNode.Parse(File.ReadAllText(Path.Combine(stage,"manifest.json")))!.AsObject();
 if(m["version"]!.GetValue<int>()!=1||m["timezone"]!.GetValue<string>()!="UTC"||string.IsNullOrEmpty(m["source"]!.GetValue<string>())||string.IsNullOrEmpty(m["symbol"]!.GetValue<string>()))throw new InvalidDataException("manifest");
 if(start!=null){
 long a=Millis(start),b=Millis(end!);if(a>b||a<Millis(m["start"]!.GetValue<string>())||b>Millis(m["end"]!.GetValue<string>()))throw new InvalidDataException("slice range");
 var selected=new JsonArray();foreach(var day in m["days"]!.AsArray())if(day!["day_start"]!.GetValue<long>()>=a && day["day_start"]!.GetValue<long>()<=b)selected.Add(day.DeepClone());
 m["days"]=selected;m["start"]=start;m["end"]=end;m.Remove("csv_sha256");
 }
 int digits=m["digits"]!.GetValue<int>();if(digits<0||digits>8)throw new InvalidDataException("digits");double factor=Math.Pow(10,digits);long lower=Millis(m["start"]!.GetValue<string>()),upper=Millis(m["end"]!.GetValue<string>())+86400000;
 long total=0,bars=0,first=0,last=0,h=0,prevday=-1;
 foreach(var dn in m["days"]!.AsArray()){
 var d=dn!;long day=d["day_start"]!.GetValue<long>();if(day<=prevday||day%86400000!=0)throw new InvalidDataException("day order");prevday=day;
 string PathFor(string kind,int width){string name=d[kind+"_file"]!.GetValue<string>();if(Path.GetFileName(name)!=name)throw new InvalidDataException("path");string path=Path.Combine(stage,name);if(new FileInfo(path).Length!=d[kind]!.GetValue<long>()*width||Sha(path)!=d[kind+"_sha256"]!.GetValue<string>())throw new InvalidDataException("file integrity");return path;}
 using var tr=new BinaryReader(new FileStream(PathFor("ticks",24),FileMode.Open,FileAccess.Read,FileShare.Read,262144));using var br=new BinaryReader(File.OpenRead(PathFor("bars",60)));
 long nt=0,nb=0;Bar? bar=null;
 void CheckBar(){if(bar!=null){if(bar!=Bar.Read(br))throw new InvalidDataException("M1 mismatch");bars++;nb++;}}
 long tickBytes=tr.BaseStream.Length;
 for(long offset=0;offset<tickBytes;offset+=24){long ms=tr.ReadInt64();double b=tr.ReadDouble(),a=tr.ReadDouble();CheckTick(ms,b,a,factor,lower,upper,total==0?null:last);if(ms/86400000*86400000!=day)throw new InvalidDataException("day");if(bar!=null&&bar.Value.Time!=ms/60000*60){CheckBar();bar=null;}bar=Update(bar,ms,b,a,factor);h=Hash(h,ms,b,a,factor);if(total==0)first=ms;last=ms;total++;nt++;}
 CheckBar();if(br.BaseStream.Position!=br.BaseStream.Length||nt!=d["ticks"]!.GetValue<long>()||nb!=d["bars"]!.GetValue<long>())throw new InvalidDataException("daily counts");
 }
 if(start!=null){if(total<=0)throw new InvalidDataException("empty slice");m["ticks"]=total;m["bars"]=bars;m["first_msc"]=first;m["last_msc"]=last;m["tick_hash"]=h;return m;}
 if(total<=0||total!=m["ticks"]!.GetValue<long>()||bars!=m["bars"]!.GetValue<long>()||first!=m["first_msc"]!.GetValue<long>()||last!=m["last_msc"]!.GetValue<long>()||h!=m["tick_hash"]!.GetValue<long>())throw new InvalidDataException("totals/hash");return m;
}
public static int Run(string[] a){try{var m=a[0]=="prepare"?Prepare(a[1],a[2],a[3],a[4],int.Parse(a[5]),a[6],a[7]):a[0]=="slice"?Validate(a[1],a[2],a[3]):Validate(a[1]);Console.WriteLine(m.ToJsonString());return 0;}catch(Exception e){Console.Error.WriteLine(e.Message);return 1;}}
}

