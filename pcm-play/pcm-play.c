/*
 * pcm-play: play raw PCM from stdin on the default Windows audio device (WASAPI, shared mode).
 *
 *   shairport-sync ... | pcm-play [rate] [channels]        (defaults: 44100 2, signed 16-bit LE)
 *
 * Shairport Sync already paces its output against the AirPlay (PTP) clock, so this program
 * plays whatever arrives as soon as there is room in a short device buffer; when no data
 * arrives (paused), WASAPI plays silence. Exits with status 2 if the audio device goes away
 * so a supervisor can restart it on the new default device.
 *
 * MIT License, part of airplay2-windows.
 */
#define COBJMACROS
#include <windows.h>
#include <initguid.h>
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <fcntl.h>
#include <io.h>
#include <stdio.h>
#include <stdlib.h>

#define BUFFER_MS 200   /* device buffer length */
#define CHUNK_MS 10     /* how much stdin is read at a time */

DEFINE_GUID(CLSID_MMDeviceEnumerator_, 0xBCDE0395, 0xE52F, 0x467C, 0x8E, 0x3D, 0xC4, 0x57, 0x92, 0x91, 0x69, 0x2E);
DEFINE_GUID(IID_IMMDeviceEnumerator_, 0xA95664D2, 0x9614, 0x4F35, 0xA7, 0x46, 0xDE, 0x8D, 0xB6, 0x36, 0x17, 0xE6);
DEFINE_GUID(IID_IAudioClient_, 0x1CB9AD4C, 0xDBFA, 0x4C32, 0xB1, 0x78, 0xC2, 0xF5, 0x68, 0xA7, 0x03, 0xB2);
DEFINE_GUID(IID_IAudioRenderClient_, 0xF294ACFC, 0x3146, 0x4483, 0xA7, 0xBF, 0xAD, 0xDC, 0xA7, 0xC2, 0x60, 0xE2);

static void fail(const char *what, HRESULT hr, int code) {
    fprintf(stderr, "pcm-play: %s failed (hr=0x%08lx)\n", what, (unsigned long)hr);
    exit(code);
}

int main(int argc, char **argv) {
    unsigned rate = argc > 1 ? (unsigned)atoi(argv[1]) : 44100;
    unsigned channels = argc > 2 ? (unsigned)atoi(argv[2]) : 2;
    unsigned frame_bytes = channels * 2;

    _setmode(_fileno(stdin), _O_BINARY);
    setvbuf(stderr, NULL, _IONBF, 0);

    HRESULT hr = CoInitializeEx(NULL, COINIT_MULTITHREADED);
    if (FAILED(hr)) fail("CoInitializeEx", hr, 1);

    IMMDeviceEnumerator *enumerator = NULL;
    hr = CoCreateInstance(&CLSID_MMDeviceEnumerator_, NULL, CLSCTX_ALL, &IID_IMMDeviceEnumerator_,
                          (void **)&enumerator);
    if (FAILED(hr)) fail("CoCreateInstance(MMDeviceEnumerator)", hr, 1);

    IMMDevice *device = NULL;
    hr = IMMDeviceEnumerator_GetDefaultAudioEndpoint(enumerator, eRender, eConsole, &device);
    if (FAILED(hr)) fail("GetDefaultAudioEndpoint", hr, 2);

    IAudioClient *client = NULL;
    hr = IMMDevice_Activate(device, &IID_IAudioClient_, CLSCTX_ALL, NULL, (void **)&client);
    if (FAILED(hr)) fail("Activate(IAudioClient)", hr, 2);

    WAVEFORMATEX fmt = {0};
    fmt.wFormatTag = WAVE_FORMAT_PCM;
    fmt.nChannels = (WORD)channels;
    fmt.nSamplesPerSec = rate;
    fmt.wBitsPerSample = 16;
    fmt.nBlockAlign = (WORD)frame_bytes;
    fmt.nAvgBytesPerSec = rate * frame_bytes;

    /* AUTOCONVERTPCM lets the engine resample 44.1 kHz to whatever the device mix format is */
    hr = IAudioClient_Initialize(client, AUDCLNT_SHAREMODE_SHARED,
                                 AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY,
                                 (REFERENCE_TIME)BUFFER_MS * 10000, 0, &fmt, NULL);
    if (FAILED(hr)) fail("IAudioClient::Initialize", hr, 2);

    UINT32 buffer_frames = 0;
    IAudioClient_GetBufferSize(client, &buffer_frames);

    IAudioRenderClient *render = NULL;
    hr = IAudioClient_GetService(client, &IID_IAudioRenderClient_, (void **)&render);
    if (FAILED(hr)) fail("GetService(IAudioRenderClient)", hr, 2);

    fprintf(stderr, "pcm-play: %u Hz, %u ch, device buffer %u frames\n", rate, channels, buffer_frames);

    UINT32 chunk_frames = rate * CHUNK_MS / 1000;
    unsigned char *chunk = malloc((size_t)chunk_frames * frame_bytes);
    int started = 0;

    for (;;) {
        size_t got = fread(chunk, 1, (size_t)chunk_frames * frame_bytes, stdin);
        UINT32 frames = (UINT32)(got / frame_bytes);
        if (frames == 0)
            break; /* EOF */

        UINT32 written = 0;
        while (written < frames) {
            UINT32 padding = 0;
            hr = IAudioClient_GetCurrentPadding(client, &padding);
            if (hr == AUDCLNT_E_DEVICE_INVALIDATED) fail("device lost", hr, 2);
            UINT32 room = buffer_frames - padding;
            if (room == 0) {
                Sleep(2);
                continue;
            }
            UINT32 n = frames - written < room ? frames - written : room;
            BYTE *dst = NULL;
            hr = IAudioRenderClient_GetBuffer(render, n, &dst);
            if (hr == AUDCLNT_E_DEVICE_INVALIDATED) fail("device lost", hr, 2);
            if (FAILED(hr)) fail("GetBuffer", hr, 2);
            memcpy(dst, chunk + (size_t)written * frame_bytes, (size_t)n * frame_bytes);
            IAudioRenderClient_ReleaseBuffer(render, n, 0);
            written += n;
            if (!started) {
                IAudioClient_Start(client);
                started = 1;
            }
        }
    }

    /* drain what is still queued, then stop */
    UINT32 padding = 0;
    while (started && SUCCEEDED(IAudioClient_GetCurrentPadding(client, &padding)) && padding > 0)
        Sleep(10);
    IAudioClient_Stop(client);
    return 0;
}
