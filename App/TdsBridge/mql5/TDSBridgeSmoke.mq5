#property strict
#property version "1.00"
#property description "No-trade tester EA: checks every tick time and bid/ask sequence against TDS export."
input string ResultFile="TDSBridge\\test-result.json";
input long ExpectedTicks=0;
input long ExpectedFirst=0;
input long ExpectedLast=0;
input long ExpectedHash=0;
input int PriceDigits=3;
long count=0,first=0,last=0,hash=0;
bool valid=true;

int OnInit()
{
   if(!MQLInfoInteger(MQL_TESTER) || ExpectedTicks<=0 || StringFind(_Symbol,"TDS_")!=0)
      return INIT_PARAMETERS_INCORRECT;
   Print("TDSBridge smoke start ",_Symbol,"; no trading; expected ticks=",ExpectedTicks);
   return INIT_SUCCEEDED;
}

void OnTick()
{
   MqlTick t;
   if(!SymbolInfoTick(_Symbol,t)) {valid=false;return;}
   if(count==0) first=t.time_msc;
   if(t.time_msc<last || t.bid<=0 || t.ask<t.bid) valid=false;
   last=t.time_msc; count++;
   long factor=(long)MathPow(10,PriceDigits);
   hash=(hash*31+t.time_msc%2147483647+(long)MathRound(t.bid*factor)*3+(long)MathRound(t.ask*factor)*7)%2147483647;
}

double OnTester()
{
   bool ok=valid && count==ExpectedTicks && first==ExpectedFirst && last==ExpectedLast && hash==ExpectedHash;
   string body=StringFormat("{\"ok\":%s,\"symbol\":\"%s\",\"ticks\":%I64d,\"first_msc\":%I64d,\"last_msc\":%I64d,\"tick_hash\":%I64d,\"orders_sent\":0}",
                            ok?"true":"false",_Symbol,count,first,last,hash);
   int f=FileOpen(ResultFile+".tmp",FILE_WRITE|FILE_TXT|FILE_ANSI|FILE_COMMON,0,CP_UTF8);
   if(f!=INVALID_HANDLE)
   {
      FileWriteString(f,body); FileFlush(f); FileClose(f);
      FileMove(ResultFile+".tmp",FILE_COMMON,ResultFile,FILE_COMMON|FILE_REWRITE);
   }
   Print("TDSBridge TEST ",ok?"PASS":"FAIL",": ",body);
   return ok?1.0:0.0;
}
