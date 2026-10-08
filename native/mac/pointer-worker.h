#pragma once
#include <CoreGraphics/CoreGraphics.h>
#include "input.h"

typedef struct RcfPointerWorker RcfPointerWorker;
typedef void (*RcfPointerRelease)(uint32_t code, double timestamp);

RcfPointerWorker *rcf_pointer_begin(RcfMacNotify notify);
uint32_t rcf_pointer_state(RcfPointerWorker *worker);
int32_t rcf_pointer_drain(RcfPointerWorker *worker, CGEventRef event, RcfRelativeMotionHandler receiver, RcfPointerRelease release);
uint32_t rcf_pointer_pump(RcfPointerWorker *worker, RcfRelativeMotionHandler receiver, RcfPointerRelease release);
uint32_t rcf_pointer_pending(RcfPointerWorker *worker);
double rcf_pointer_boundary(RcfPointerWorker *worker);
void rcf_pointer_discard(RcfPointerWorker *worker);
void rcf_pointer_reconcile(RcfPointerWorker *worker, uint32_t code);
uint32_t rcf_pointer_validate(RcfPointerWorker *worker, double now, bool tracking);
uint32_t rcf_pointer_initial_key(RcfPointerWorker *worker, uint32_t code);
uint32_t rcf_pointer_error(RcfPointerWorker *worker);
double rcf_pointer_started_at(RcfPointerWorker *worker);
int32_t rcf_pointer_end(RcfPointerWorker *worker);
