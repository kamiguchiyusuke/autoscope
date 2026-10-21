namespace MT4BatchReporter;
public static class GifValidator
{
    // Validate the complete GIF container, including color tables, every image
    // data sub-block, and the terminating trailer. A header alone is not an image.
    public static bool IsComplete(string path)
    {
        var info=new FileInfo(path);if(info.Length<14||info.Length>100*1024*1024)return false;
        byte[] b=File.ReadAllBytes(path);int p=13,images=0;
        if(!(b.AsSpan(0,6).SequenceEqual("GIF89a"u8)||b.AsSpan(0,6).SequenceEqual("GIF87a"u8))||BitConverter.ToUInt16(b,6)==0||BitConverter.ToUInt16(b,8)==0)return false;
        bool Skip(int n){if(n<0||p>b.Length-n)return false;p+=n;return true;}
        bool Blocks(bool dataRequired)
        {
            int total=0;
            while(p<b.Length){int n=b[p++];if(n==0)return !dataRequired||total>0;if(!Skip(n))return false;total+=n;}
            return false;
        }
        if((b[10]&128)!=0&&!Skip(3*(1<<((b[10]&7)+1))))return false;
        while(p<b.Length)
        {
            byte tag=b[p++];
            if(tag==0x3b)return images>0&&p==b.Length;
            if(tag==0x21){if(!Skip(1)||!Blocks(false))return false;continue;}
            if(tag!=0x2c||p>b.Length-9)return false;
            if(BitConverter.ToUInt16(b,p+4)==0||BitConverter.ToUInt16(b,p+6)==0)return false;
            byte packed=b[p+8];p+=9;
            if((packed&128)!=0&&!Skip(3*(1<<((packed&7)+1))))return false;
            if(p>=b.Length||b[p]<2||b[p]>8)return false;p++;
            if(!Blocks(true))return false;images++;
        }
        return false;
    }
}
