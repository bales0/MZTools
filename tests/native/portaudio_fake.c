/* Independent device-free ABI fixture. Not linked or shipped with production. */
#include <stdlib.h>
#include <stdint.h>
#include <stdatomic.h>
#include <pthread.h>
#include <time.h>
typedef int (*callback_t)(const void*,void*,unsigned long,const void*,unsigned long,void*);
typedef void (*finished_t)(void*);
typedef struct {int version;const char* name;int api,inputs,outputs;double lowin,lowout,highin,highout,rate;} device_t;
typedef struct {int device,channels;unsigned long format;double latency;void* info;} params_t;
typedef struct {int version;double in,out,rate;} info_t;
typedef struct {double adc,now,dac;} times_t;
typedef struct {callback_t callback;finished_t finished;void* user;int mode,started,joined;atomic_int stop;pthread_t worker;info_t info;} stream_t;
static atomic_int mode,streams;
static device_t device={2,"Test fixture only",0,2,0,.01,0,.02,0,48000};
void mz_mode(int m){atomic_store(&mode,m);}
int mz_open_streams(void){return atomic_load(&streams);}
int Pa_Initialize(void){return 0;}
int Pa_GetDeviceCount(void){return 1;}
const void* Pa_GetDeviceInfo(int i){return i==0?&device:NULL;}
const char* Pa_GetErrorText(int error){return "Injected transport error";}
int Pa_IsFormatSupported(const params_t* p,const void* out,double rate){return p->channels==2&&p->format==8&&rate==48000?0:-9997;}
int Pa_OpenStream(void** result,const params_t* in,const void* out,double rate,unsigned long frames,unsigned long flags,callback_t callback,void* user)
{
 if(atomic_load(&mode)==2)return -9996;
 stream_t* s=calloc(1,sizeof(*s));s->mode=atomic_load(&mode);s->callback=callback;s->user=user;s->info.version=1;s->info.rate=s->mode==3?96000:rate;*result=s;atomic_fetch_add(&streams,1);return 0;
}
int Pa_SetStreamFinishedCallback(void* stream,finished_t callback){((stream_t*)stream)->finished=callback;return 0;}
const void* Pa_GetStreamInfo(void* stream){return &((stream_t*)stream)->info;}
static void* produce(void* stream)
{
 stream_t* s=stream;int64_t first=0;int packet=0;short data[1024*2];
 while(!atomic_load(&s->stop))
 {
  int n=(packet++%2)?511:128;for(int i=0;i<n;i++){data[i*2]=(short)((first+i)%32000);data[i*2+1]=(short)-data[i*2];}
  times_t t={first/48000.0,first/48000.0,0};unsigned long flags=(s->mode==5&&packet==3)?2:0;
  if(s->mode==8&&packet==1)s->callback(NULL,NULL,0,&t,0,s->user);
  if(s->mode==9&&packet==3)t.adc=0.0/0.0;
  int result=s->callback(s->mode==6&&packet==3?NULL:data,NULL,n,&t,flags,s->user);first+=n;
  if(result!=0||(s->mode==7&&first>=3000))break;
  struct timespec delay={0,1000000};nanosleep(&delay,NULL);
 }
 if(s->finished)s->finished(s->user);return NULL;
}
int Pa_StartStream(void* stream){stream_t* s=stream;if(s->mode==4)return -9998;s->started=1;return pthread_create(&s->worker,NULL,produce,s)?-9999:0;}
int Pa_StopStream(void* stream){stream_t* s=stream;atomic_store(&s->stop,1);if(s->started&&!s->joined){pthread_join(s->worker,NULL);s->joined=1;}return 0;}
int Pa_CloseStream(void* stream){Pa_StopStream(stream);free(stream);atomic_fetch_sub(&streams,1);return 0;}
