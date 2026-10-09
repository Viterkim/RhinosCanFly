#pragma once
#include "input.h"

typedef struct RcfPointerWorker RcfPointerWorker;
RcfPointerWorker *rcf_pointer_begin(RcfMacNotify notify);
double rcf_pointer_clock(void);
int32_t rcf_pointer_activate(RcfPointerWorker *worker, const RcfCaptureConfig *config);
int32_t rcf_pointer_finish(RcfPointerWorker *worker, uint32_t reason);
uint32_t rcf_pointer_state(RcfPointerWorker *worker);
uint32_t rcf_pointer_pump(RcfPointerWorker *worker, RcfMacHandler receiver);
uint32_t rcf_pointer_pending(RcfPointerWorker *worker);
double rcf_pointer_boundary(RcfPointerWorker *worker);
void rcf_pointer_discard(RcfPointerWorker *worker);
uint32_t rcf_pointer_initial_key(RcfPointerWorker *worker, uint32_t code);
uint32_t rcf_pointer_validate(RcfPointerWorker *worker);
uint32_t rcf_pointer_error(RcfPointerWorker *worker);
uint32_t rcf_pointer_guard(RcfPointerWorker *worker, RcfCaptureEvent event);
uint32_t rcf_pointer_guards_pending(RcfPointerWorker *worker);
double rcf_pointer_started_at(RcfPointerWorker *worker);
int32_t rcf_pointer_end(RcfPointerWorker *worker);
uint32_t rcf_pointer_diagnostics(RcfPointerWorker *worker, char *destination, uint32_t capacity);
