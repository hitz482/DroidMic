/* Host-side unit tests for driver/VirtualAudioDevice/ringbuffer.h.
 * Build: cc -std=c99 -Wall -Wextra -Werror -o ringbuffer_test ringbuffer_test.c && ./ringbuffer_test */
#include <stdio.h>
#include <stdlib.h>
#include "../VirtualAudioDevice/ringbuffer.h"

static int failures = 0;
#define CHECK(c) do { if (!(c)) { printf("FAIL %s:%d: %s\n", __FILE__, __LINE__, #c); failures++; } } while (0)

int main(void)
{
    short storage[8], out[16], in[16];
    DROIDMIC_RING r;
    int i;
    for (i = 0; i < 16; i++) in[i] = (short)(i + 1);

    DroidMicRingInit(&r, storage, 8);
    DroidMicRingWrite(&r, in, 5);
    CHECK(r.Fill == 5);
    CHECK(DroidMicRingRead(&r, out, 3, 1) == 3);
    CHECK(out[0] == 1 && out[2] == 3);

    /* Wrap-around write and read. */
    DroidMicRingWrite(&r, in + 5, 5);          /* fill = 7, wraps */
    CHECK(r.Fill == 7);
    CHECK(DroidMicRingRead(&r, out, 7, 1) == 7);
    for (i = 0; i < 7; i++) CHECK(out[i] == i + 4);

    /* Underrun pads with silence and is counted. */
    DroidMicRingWrite(&r, in, 2);
    CHECK(DroidMicRingRead(&r, out, 5, 1) == 2);
    CHECK(out[0] == 1 && out[1] == 2 && out[2] == 0 && out[4] == 0);
    CHECK(r.Underruns == 3);
    CHECK(DroidMicRingRead(&r, out, 4, 0) == 0);
    CHECK(r.Underruns == 3);

    /* Overflow keeps the newest samples. */
    DroidMicRingClear(&r);
    DroidMicRingWrite(&r, in, 6);
    DroidMicRingWrite(&r, in + 6, 4);          /* 10 > 8: drop 2 oldest */
    CHECK(r.Fill == 8);
    CHECK(r.Overflows == 2);
    CHECK(DroidMicRingRead(&r, out, 8, 1) == 8);
    for (i = 0; i < 8; i++) CHECK(out[i] == i + 3);

    /* Write larger than capacity keeps the last `capacity` samples. */
    DroidMicRingWrite(&r, in, 12);
    CHECK(r.Fill == 8);
    CHECK(DroidMicRingRead(&r, out, 8, 1) == 8);
    CHECK(out[0] == 5 && out[7] == 12);

    /* Trim drops the oldest. */
    DroidMicRingWrite(&r, in, 6);
    DroidMicRingTrim(&r, 2);
    CHECK(r.Fill == 2);
    CHECK(DroidMicRingRead(&r, out, 2, 1) == 2);
    CHECK(out[0] == 5 && out[1] == 6);

    /* Long random soak against a reference model. */
    {
        short big[97];
        DROIDMIC_RING s;
        short model[100000];
        unsigned long head = 0, tail = 0; /* model queue */
        short next = 0;
        int iter;
        srand(1);
        DroidMicRingInit(&s, big, 97);
        for (iter = 0; iter < 20000; iter++) {
            unsigned long n = (unsigned long)(rand() % 40), k;
            if (rand() & 1) {
                short tmp[40];
                for (k = 0; k < n; k++) tmp[k] = next++;
                DroidMicRingWrite(&s, tmp, n);
                for (k = 0; k < n; k++) model[tail++ % 100000] = tmp[k];
                while (tail - head > 97) head++;
            } else {
                short got[40];
                unsigned long real = DroidMicRingRead(&s, got, n, 1);
                unsigned long expect = (tail - head) < n ? (tail - head) : n;
                CHECK(real == expect);
                for (k = 0; k < real; k++) { CHECK(got[k] == model[head % 100000]); head++; }
                for (k = real; k < n; k++) CHECK(got[k] == 0);
            }
            CHECK(s.Fill == tail - head);
        }
    }

    if (failures == 0) printf("ringbuffer: all tests passed\n");
    return failures == 0 ? 0 : 1;
}
