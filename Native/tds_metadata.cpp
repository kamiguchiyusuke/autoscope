#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <atomic>
#include <thread>
#include <vector>
#include <cstdint>
struct FP { uint64_t volume,low,high,size; int64_t write,change; };
extern "C" __declspec(dllexport) DWORD metadata(const wchar_t** paths, unsigned count, FP* output, unsigned workers) noexcept {
 if(!paths||!output||count>1000000||workers<1||workers>16)return ERROR_INVALID_PARAMETER;
 std::atomic<unsigned> next{0}; std::atomic<DWORD> error{0};
 auto run=[&]{for(;;){unsigned i=next.fetch_add(1);if(i>=count||error.load())break;
 HANDLE h=CreateFileW(paths[i],FILE_READ_ATTRIBUTES,7,nullptr,OPEN_EXISTING,FILE_FLAG_OPEN_REPARSE_POINT,nullptr);
 if(h==INVALID_HANDLE_VALUE){error.store(GetLastError());break;}
 FILE_BASIC_INFO b{};FILE_ID_INFO id{};FILE_STANDARD_INFO s{};
 bool ok=GetFileInformationByHandleEx(h,FileBasicInfo,&b,sizeof(b))&&GetFileInformationByHandleEx(h,FileIdInfo,&id,sizeof(id))&&GetFileInformationByHandleEx(h,FileStandardInfo,&s,sizeof(s));
 DWORD err=ok?0:GetLastError();CloseHandle(h);
 if(!ok){error.store(err?err:ERROR_INVALID_DATA);break;}
 if(!b.ChangeTime.QuadPart||(b.FileAttributes&(FILE_ATTRIBUTE_REPARSE_POINT|FILE_ATTRIBUTE_DIRECTORY))){error.store(ERROR_INVALID_DATA);break;}
 uint64_t words[2];memcpy(words,id.FileId.Identifier,16);output[i]={id.VolumeSerialNumber,words[0],words[1],(uint64_t)s.EndOfFile.QuadPart,b.LastWriteTime.QuadPart,b.ChangeTime.QuadPart};
 }};
 try {std::vector<std::jthread> pool;for(unsigned i=1;i<workers;i++)pool.emplace_back(run);run();}catch(...){return ERROR_NOT_ENOUGH_MEMORY;}
 return error.load();
}
