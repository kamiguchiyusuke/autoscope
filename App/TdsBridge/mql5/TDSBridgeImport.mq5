#property strict
#property version "1.00"
#property description "Imports validated TDSBridge files into dedicated custom symbols. No trading."
input string Job="TDSBridge\\job.txt";

string folder="";
string target="";
long reused_ticks=0,written_ticks=0,reused_bars=0;

bool Receipt(string body)
{
   string tmp=folder+"import-result.tmp";
   int f=FileOpen(tmp,FILE_WRITE|FILE_TXT|FILE_ANSI|FILE_COMMON,0,CP_UTF8);
   if(f==INVALID_HANDLE) return false;
   FileWriteString(f,body); FileFlush(f); FileClose(f);
   return FileMove(tmp,FILE_COMMON,folder+"import-result.json",FILE_COMMON|FILE_REWRITE);
}

void Fail(string message)
{
   Print("TDSBridge ERROR: ",message," / error=",GetLastError());
   StringReplace(message,"\\","/"); StringReplace(message,"\"","'");
   Receipt("{\"ok\":false,\"message\":\""+message+"\",\"error\":"+IntegerToString(GetLastError())+"}");
}

long HashTick(long hash,const MqlTick &t,int digits)
{
   long factor=(long)MathPow(10,digits);
   return (hash*31+t.time_msc%2147483647+(long)MathRound(t.bid*factor)*3+(long)MathRound(t.ask*factor)*7)%2147483647;
}

// At most one calendar year of M1 bars is resident; ticks remain day-sized.
bool ReadRates(int file,long begin,int count,MqlRates &rates[])
{
   if(ArrayResize(rates,count)!=count) return false;
   ZeroMemory(rates);
   bool valid=true;
   for(int i=0;i<count;i++)
      {
         rates[i].time=(datetime)FileReadLong(file);
         rates[i].open=FileReadDouble(file); rates[i].high=FileReadDouble(file);
         rates[i].low=FileReadDouble(file); rates[i].close=FileReadDouble(file);
         rates[i].tick_volume=FileReadLong(file); rates[i].spread=FileReadInteger(file,INT_VALUE);
         rates[i].real_volume=FileReadLong(file);
         if((long)rates[i].time<begin/1000 || (long)rates[i].time>=(begin+86400000)/1000 ||
            (long)rates[i].time%60!=0 || (i>0 && rates[i].time<=rates[i-1].time)) valid=false;
      }
   return valid;
}

bool FlushRates(MqlRates &rates[],long begin,long end)
{
   int count=ArraySize(rates);
   if(count==0) return true;
   MqlRates existing[];
   int got=CopyRates(target,PERIOD_M1,(datetime)(begin/1000),(datetime)((end+86399999)/1000),existing);
   bool same=got==count;
   for(int i=0;same && i<count;i++)
      same=existing[i].time==rates[i].time && existing[i].open==rates[i].open &&
           existing[i].high==rates[i].high && existing[i].low==rates[i].low &&
           existing[i].close==rates[i].close && existing[i].tick_volume==rates[i].tick_volume &&
           existing[i].spread==rates[i].spread && existing[i].real_volume==rates[i].real_volume;
   ArrayFree(existing);
   if(same){reused_bars+=count;ArrayFree(rates);return true;}
   ResetLastError();
   if(CustomRatesReplace(target,(datetime)(begin/1000),(datetime)((end+86399999)/1000),rates)!=count)
   {Fail("M1 batch replace returned incomplete count");return false;}
   PrintFormat("TDSBridge M1 preloaded: %s through %s / %d bars",TimeToString((datetime)(begin/1000),TIME_DATE),TimeToString((datetime)(end/1000),TIME_DATE),count);
   ArrayFree(rates);
   return true;
}

bool PreloadRates(int job,int days,long expected_bars)
{
   MqlRates batch[],day_rates[];
   long begin_batch=0,previous=-1,total=0;
   int batch_year=0;
   for(int day=0;day<days;day++)
   {
      if(IsStopped()) {Fail("Import stopped during M1 preload");return false;}
      long begin=StringToInteger(FileReadString(job));
      string tickfile=FileReadString(job);
      int nt=(int)StringToInteger(FileReadString(job));
      string barfile=FileReadString(job);
      int nb=(int)StringToInteger(FileReadString(job));
      if(nt<=0 || nt>10000000 || nb<=0 || nb>1440 || begin<0 || begin%86400000!=0 ||
         (previous>=0 && begin<=previous) || StringFind(tickfile,"..")>=0 || StringFind(barfile,"..")>=0)
      {Fail("Invalid day header during M1 preload");return false;}
      MqlDateTime date;
      if(!TimeToStruct((datetime)(begin/1000),date)) {Fail("Invalid M1 batch date");return false;}
      if(ArraySize(batch)>0 && (date.year!=batch_year || begin!=previous+86400000))
         if(!FlushRates(batch,begin_batch,previous)) return false;
      if(ArraySize(batch)==0) {begin_batch=begin;batch_year=date.year;}
      int bf=FileOpen(folder+barfile,FILE_READ|FILE_BIN|FILE_COMMON|FILE_SHARE_READ);
      if(bf==INVALID_HANDLE) {Fail("Staged M1 file missing");return false;}
      if(FileSize(bf)!=(ulong)nb*60) {FileClose(bf);Fail("Staged M1 file size mismatch");return false;}
      bool valid=ReadRates(bf,begin,nb,day_rates);
      FileClose(bf);
      if(!valid) {Fail("Invalid staged M1 time ordering");return false;}
      int offset=ArraySize(batch);
      if(offset+nb>366*1440 || ArrayResize(batch,offset+nb,1440*31)!=offset+nb ||
         ArrayCopy(batch,day_rates,offset,0,nb)!=nb)
      {Fail("Cannot allocate M1 batch");return false;}
      total+=nb;previous=begin;
   }
   if(total!=expected_bars) {Fail("M1 manifest count mismatch");return false;}
   return FlushRates(batch,begin_batch,previous);
}

// Read M1 only after all tick writes, avoiding write/read interleaving.
long VerifyRates(int job,int days)
{
   long total=0;
   for(int day=0;day<days;day++)
   {
      if(IsStopped()) {Fail("Import stopped during M1 verification");return -1;}
      ulong day_start_us=GetMicrosecondCount();
      long begin=StringToInteger(FileReadString(job));
      string tickfile=FileReadString(job);
      int nt=(int)StringToInteger(FileReadString(job));
      string barfile=FileReadString(job);
      int nb=(int)StringToInteger(FileReadString(job));
      if(nt<=0 || nb<=0 || nb>1440 || begin%86400000!=0 || StringFind(barfile,"..")>=0)
      {Fail("Invalid M1 verification header");return -1;}
      int bf=FileOpen(folder+barfile,FILE_READ|FILE_BIN|FILE_COMMON|FILE_SHARE_READ);
      if(bf==INVALID_HANDLE) {Fail("Staged M1 file missing");return -1;}
      if(FileSize(bf)!=(ulong)nb*60) {FileClose(bf);Fail("Staged M1 file size mismatch");return -1;}
      MqlRates rates[];
      bool valid=ReadRates(bf,begin,nb,rates);
      FileClose(bf);
      if(!valid) {Fail("Invalid staged M1 time ordering");return -1;}
      MqlRates actual_rates[];
      int bar_count=-1,retries=0,first_count=-1,first_error=0;
      ulong rates_api_us=0,first_rates_us=0;
      for(int retry=0;retry<100;retry++)
      {
         ResetLastError();
         ulong rates_start_us=GetMicrosecondCount();
         bar_count=CopyRates(target,PERIOD_M1,(datetime)(begin/1000),(datetime)((begin+86399999)/1000),actual_rates);
         ulong rates_elapsed_us=GetMicrosecondCount()-rates_start_us;
         rates_api_us+=rates_elapsed_us;
         if(retry==0) {first_count=bar_count;first_error=GetLastError();first_rates_us=rates_elapsed_us;}
         if(bar_count==nb) break;
         retries++;
         Sleep(100);
      }
      if(bar_count!=nb) {Fail("M1 readback count mismatch");return -1;}
      for(int i=0;i<nb;i++)
         if(actual_rates[i].time!=rates[i].time || actual_rates[i].open!=rates[i].open ||
            actual_rates[i].high!=rates[i].high || actual_rates[i].low!=rates[i].low ||
            actual_rates[i].close!=rates[i].close || actual_rates[i].tick_volume!=rates[i].tick_volume)
         {Fail("M1 readback values differ");return -1;}
      total+=bar_count;
      PrintFormat("TDSBridge timing B M1 day=%d ratesRead_ms=%.3f retry=%d firstRates_ms=%.3f firstCount=%d firstError=%d day_ms=%.3f",day+1,rates_api_us/1000.0,retries,first_rates_us/1000.0,first_count,first_error,(GetMicrosecondCount()-day_start_us)/1000.0);
   }
   return total;
}

void OnStart()
{
   int pos=-1;
   for(int j=0;j<StringLen(Job);j++) if(StringGetCharacter(Job,j)==92) pos=j;
   if(pos<0 || StringFind(Job,"..")>=0 || StringFind(Job,"TDSBridge\\")!=0) {Print("Invalid Job path");return;}
   folder=StringSubstr(Job,0,pos+1);
   int job=FileOpen(Job,FILE_READ|FILE_TXT|FILE_ANSI|FILE_COMMON);
   if(job==INVALID_HANDLE) {Fail("Cannot open job");return;}
   string magic=FileReadString(job);
   target=FileReadString(job);
   string origin=FileReadString(job);
   int digits=(int)StringToInteger(FileReadString(job));
   long expected=StringToInteger(FileReadString(job));
   long expected_bars=StringToInteger(FileReadString(job));
   long expected_first=StringToInteger(FileReadString(job));
   long expected_last=StringToInteger(FileReadString(job));
   long expected_hash=StringToInteger(FileReadString(job));
   int days=(int)StringToInteger(FileReadString(job));
   if(magic!="TDSBRIDGE1" || StringFind(target,"_TDS_UTC")!=StringLen(target)-8 || StringLen(target)>31 ||
      digits<0 || digits>8 || expected<=0 || days<1 || expected_last<expected_first)
   {FileClose(job);Fail("Invalid job header");return;}
   bool is_custom=false;
   if(!SymbolExist(origin,is_custom) || is_custom) {FileClose(job);Fail("Broker origin symbol missing: "+origin);return;}
   bool exists=SymbolExist(target,is_custom);
   if(exists && (!is_custom || SymbolInfoString(target,SYMBOL_DESCRIPTION)!="TDSBridge UTC v1 / "+origin))
   {FileClose(job);Fail("Target is not owned by TDSBridge");return;}
   if(!exists)
   {
      if(!CustomSymbolCreate(target,"TDSBridge",origin)) {FileClose(job);Fail("CustomSymbolCreate failed");return;}
      if(!CustomSymbolSetInteger(target,SYMBOL_DIGITS,digits) ||
         !CustomSymbolSetDouble(target,SYMBOL_POINT,MathPow(10,-digits)) ||
         !CustomSymbolSetDouble(target,SYMBOL_TRADE_TICK_SIZE,MathPow(10,-digits)) ||
         !CustomSymbolSetInteger(target,SYMBOL_CHART_MODE,SYMBOL_CHART_MODE_BID) ||
         !CustomSymbolSetString(target,SYMBOL_DESCRIPTION,"TDSBridge UTC v1 / "+origin))
      {FileClose(job);Fail("Custom symbol specification failed");return;}
      // UTC history includes rollover/weekend quotes; do not drop them using broker-local sessions.
      for(int d=0;d<7;d++)
      {
         if(!CustomSymbolSetSessionQuote(target,(ENUM_DAY_OF_WEEK)d,0,0,86400) ||
            !CustomSymbolSetSessionTrade(target,(ENUM_DAY_OF_WEEK)d,0,0,86400))
         {FileClose(job);Fail("Session configuration failed");return;}
      }
   }
   else if(SymbolInfoInteger(target,SYMBOL_DIGITS)!=digits)
   {FileClose(job);Fail("Existing target digits differ; use another symbol");return;}
   // Forex custom names must begin with the original currency pair.
   ENUM_SYMBOL_INFO_STRING currencies[]={SYMBOL_CURRENCY_BASE,SYMBOL_CURRENCY_PROFIT,SYMBOL_CURRENCY_MARGIN};
   for(int c=0;c<ArraySize(currencies);c++)
   {
      string currency=SymbolInfoString(origin,currencies[c]);
      bool changed=CustomSymbolSetString(target,currencies[c],currency);

      string observed=SymbolInfoString(target,currencies[c]);
      Print("TDS currency ",EnumToString(currencies[c])," expected=",currency," actual=",observed," set=",changed);
      if(StringLen(currency)<3 || !changed || observed!=currency)
      {FileClose(job);Fail("Currency specification mismatch: "+EnumToString(currencies[c])+" expected="+currency+" actual="+observed);return;}
   }
   if(SymbolInfoDouble(target,SYMBOL_TRADE_CONTRACT_SIZE)<=0)
   {FileClose(job);Fail("Invalid contract size");return;}
   if(!SymbolSelect(target,true)) {FileClose(job);Fail("SymbolSelect failed");return;}
   // Preserve history on days absent from this manifest by splitting batches
   // at gaps as well as year boundaries.
   long manifest_position=(long)FileTell(job);
   if(!PreloadRates(job,days,expected_bars)) {FileClose(job);return;}
   if(!FileSeek(job,manifest_position,SEEK_SET)) {FileClose(job);Fail("Cannot rewind manifest");return;}
   long total=0,bars_total=0,hash=0,first=0,last=0;
   for(int day=0;day<days;day++)
   {
      ulong day_start_us=GetMicrosecondCount();
      long begin=StringToInteger(FileReadString(job));
      string tickfile=FileReadString(job);
      int nt=(int)StringToInteger(FileReadString(job));
      string barfile=FileReadString(job);
      int nb=(int)StringToInteger(FileReadString(job));
      if(nt<=0 || nt>10000000 || nb<=0 || nb>1440 || begin%86400000!=0 ||
         StringFind(tickfile,"..")>=0 || StringFind(barfile,"..")>=0)
      {FileClose(job);Fail("Invalid day header");return;}
      int tf=FileOpen(folder+tickfile,FILE_READ|FILE_BIN|FILE_COMMON|FILE_SHARE_READ);
      int bf=FileOpen(folder+barfile,FILE_READ|FILE_BIN|FILE_COMMON|FILE_SHARE_READ);
      if(tf==INVALID_HANDLE || bf==INVALID_HANDLE) {if(tf!=INVALID_HANDLE)FileClose(tf);if(bf!=INVALID_HANDLE)FileClose(bf);FileClose(job);Fail("Staged file missing");return;}
      if(FileSize(tf)!=(ulong)nt*24 || FileSize(bf)!=(ulong)nb*60)
      {FileClose(tf);FileClose(bf);FileClose(job);Fail("Staged file size mismatch");return;}
      MqlTick ticks[]; MqlRates rates[];
      ArrayResize(ticks,nt); ArrayResize(rates,nb);
      ZeroMemory(ticks); ZeroMemory(rates);
      bool valid=true;
      for(int i=0;i<nt;i++)
      {
         ticks[i].time_msc=FileReadLong(tf);
         ticks[i].time=(datetime)(ticks[i].time_msc/1000);
         ticks[i].bid=FileReadDouble(tf); ticks[i].ask=FileReadDouble(tf);
         ticks[i].flags=TICK_FLAG_BID|TICK_FLAG_ASK;
         if(ticks[i].time_msc<begin || ticks[i].time_msc>=begin+86400000 ||
            (i>0 && ticks[i].time_msc<ticks[i-1].time_msc) ||
            !MathIsValidNumber(ticks[i].bid) || !MathIsValidNumber(ticks[i].ask) ||
            ticks[i].bid<=0 || ticks[i].ask<ticks[i].bid) valid=false;
      }
      if(!ReadRates(bf,begin,nb,rates)) valid=false;
      FileClose(tf); FileClose(bf);
      if(!valid) {FileClose(job);Fail("Invalid staged prices or time ordering");return;}
      ResetLastError();
      ulong stage_end_us=GetMicrosecondCount();
      MqlTick actual[];
      int got=CopyTicksRange(target,actual,COPY_TICKS_ALL,(ulong)begin,(ulong)(begin+86399999));
      bool same=got==nt;
      for(int i=0;same && i<nt;i++)
         same=actual[i].time_msc==ticks[i].time_msc && actual[i].bid==ticks[i].bid && actual[i].ask==ticks[i].ask;
      if(same) reused_ticks+=nt;
      else {
         int nx=CustomTicksReplace(target,begin,begin+86399999,ticks);
         if(nx!=nt) {FileClose(job);Fail("Replace returned incomplete count");return;}
         written_ticks+=nt;
         got=CopyTicksRange(target,actual,COPY_TICKS_ALL,(ulong)begin,(ulong)(begin+86399999));
      }
      ulong replace_end_us=GetMicrosecondCount();
      if(got!=nt) {FileClose(job);Fail("Tick readback count mismatch");return;}
      ulong tickread_end_us=GetMicrosecondCount();
      for(int i=0;i<got;i++)
      {
         if(actual[i].time_msc!=ticks[i].time_msc || actual[i].bid!=ticks[i].bid || actual[i].ask!=ticks[i].ask)
         {FileClose(job);Fail("Tick readback values differ");return;}
         hash=HashTick(hash,actual[i],digits);
      }
      ulong hash_end_us=GetMicrosecondCount();
      PrintFormat("TDSBridge timing B ticks day=%d ticks=%d stage_ms=%.3f ticksReplace_ms=%.3f ticksRead_ms=%.3f hash_ms=%.3f day_ms=%.3f",day+1,nt,(stage_end_us-day_start_us)/1000.0,(replace_end_us-stage_end_us)/1000.0,(tickread_end_us-replace_end_us)/1000.0,(hash_end_us-tickread_end_us)/1000.0,(GetMicrosecondCount()-day_start_us)/1000.0);
      if(total==0) first=actual[0].time_msc;
      last=actual[got-1].time_msc; total+=got;
      PrintFormat("TDSBridge imported day %d/%d: %d ticks / M1 verification pending",day+1,days,got);
   }
   if(!FileSeek(job,manifest_position,SEEK_SET)) {FileClose(job);Fail("Cannot rewind manifest for M1 verification");return;}
   bars_total=VerifyRates(job,days);
   FileClose(job);
   if(bars_total<0) return;
   if(total!=expected || bars_total!=expected_bars || first!=expected_first || last!=expected_last || hash!=expected_hash)
   {Fail("Final manifest comparison failed");return;}
   string result=StringFormat("{\"ok\":true,\"symbol\":\"%s\",\"timezone\":\"UTC\",\"ticks\":%I64d,\"bars\":%I64d,\"first_msc\":%I64d,\"last_msc\":%I64d,\"tick_hash\":%I64d}",target,total,bars_total,first,last,hash);
   StringReplace(result,"}",StringFormat(",\"reused_ticks\":%I64d,\"written_ticks\":%I64d,\"reused_bars\":%I64d}",reused_ticks,written_ticks,reused_bars));
   Receipt(result); Print("TDSBridge SUCCESS: ",result);
}
