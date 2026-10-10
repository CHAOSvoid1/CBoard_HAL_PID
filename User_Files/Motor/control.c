#include "control.h"
#include "app_config.h"
#include <math.h>

PID_t g_speed_pid;
PID_t g_position_pid;
STSMC_t g_speed_stsmc;
NFTSMC_t g_position_nftsmc;

static volatile Control_Mode_t control_mode = CONTROL_STOP;
#if APP_DEFAULT_CONTROLLER == APP_CONTROLLER_SMC
static volatile Control_Algorithm_t control_algorithm = CONTROL_ALGORITHM_SMC;
#else
static volatile Control_Algorithm_t control_algorithm = CONTROL_ALGORITHM_PID;
#endif
static volatile float target_speed_rpm;
static float speed_reference_rpm;
static volatile float target_position_deg;
static volatile float speed_command_rpm;
static float speed_feedback_filtered_rpm;
static uint8_t speed_filter_valid;
static volatile int16_t current_command;
static volatile uint8_t control_enabled;
static volatile uint8_t position_guard_tripped;
static uint8_t position_hold_active;
static volatile float speed_ff_static_raw;
static volatile float speed_ff_slope_raw_per_rpm;
static float position_guard_start_error;
static uint16_t position_guard_ticks;

static float Control_Clamp(float value, float limit);

static float Control_Approach(float current, float target, float max_delta)
{
    if (target > current + max_delta) return current + max_delta;
    if (target < current - max_delta) return current - max_delta;
    return target;
}

static float Control_Saturate(float value)
{
    if (value > 1.0f) return 1.0f;
    if (value < -1.0f) return -1.0f;
    return value;
}

static float Control_SignedPower(float value, float exponent)
{
    if (value > 0.0f) return powf(value, exponent);
    if (value < 0.0f) return -powf(-value, exponent);
    return 0.0f;
}

static void Control_ResetSMC(void)
{
    g_speed_stsmc.integral = 0.0f;
    g_speed_stsmc.surface = 0.0f;
    g_speed_stsmc.output = 0.0f;
    g_position_nftsmc.surface = 0.0f;
    g_position_nftsmc.output_rpm = 0.0f;
}

static float Control_STSMCUpdate(float target, float measured)
{
    float smooth_sign;
    float proportional;
    float output;
    g_speed_stsmc.surface = target - measured;
    smooth_sign = Control_Saturate(g_speed_stsmc.surface / g_speed_stsmc.boundary);
    proportional = g_speed_stsmc.k1 * sqrtf(fabsf(g_speed_stsmc.surface)) * smooth_sign;
    g_speed_stsmc.integral += g_speed_stsmc.k2 * smooth_sign * APP_CONTROL_PERIOD_S;
    g_speed_stsmc.integral = Control_Clamp(g_speed_stsmc.integral,
                                            (float)APP_CURRENT_SAFE_LIMIT);
    output = proportional + g_speed_stsmc.integral;
    g_speed_stsmc.output = Control_Clamp(output, (float)APP_CURRENT_SAFE_LIMIT);
    return g_speed_stsmc.output;
}

static float Control_NFTSMCPositionUpdate(float target_deg,
                                           const Motor_Feedback_t *fb)
{
    float error = target_deg - fb->output_angle_deg;
    float smooth_sign = Control_Saturate(error / g_position_nftsmc.boundary);
    float output_speed_deg_s;
    /* Non-singular terminal manifold: e_dot = -beta*|e|^(3/5)*sign(e).
       A continuous reaching term is used instead of an ideal sign function. */
    g_position_nftsmc.surface = error;
    output_speed_deg_s = g_position_nftsmc.beta *
                         Control_SignedPower(error, 3.0f / 5.0f) +
                         g_position_nftsmc.reaching_gain * smooth_sign;
    g_position_nftsmc.output_rpm = Control_Clamp(output_speed_deg_s *
                                                  APP_GEAR_RATIO / 6.0f,
                                                  APP_POSITION_SPEED_LIMIT_RPM);
    return g_position_nftsmc.output_rpm;
}

static float Control_Clamp(float value, float limit)
{
    if (value > limit) return limit;
    if (value < -limit) return -limit;
    return value;
}

static float Control_SpeedFeedforward(float target_rpm)
{
#if APP_MOTOR_TYPE == MOTOR_TYPE_M3508
    float magnitude = fabsf(target_rpm);
    float friction;
    float feedforward_rpm;
    float low_speed_trim;
    if (magnitude < 1.0f) return 0.0f;
    /* Keep a short zero-crossing ramp, but provide the full identified
       breakaway current from 10 rpm upward.  A 50 rpm ramp left the position
       loop stalled about 5 degrees before its target in the 2026-10-10 test. */
    friction = speed_ff_static_raw * Control_Clamp(magnitude / 10.0f, 1.0f);
    /* One linear slope over-feeds the unloaded M3508 at high speed.  Keep
       the identified 0..1000 rpm slope, then use 55% of that slope.  The
       2026-10-10 1k/4k/7.4k run showed that the earlier 50% branch still
       left the PI supplying roughly 55..60 raw after the ramp. */
    if (magnitude <= 1000.0f) {
        feedforward_rpm = speed_ff_slope_raw_per_rpm * magnitude;
    } else {
        feedforward_rpm = speed_ff_slope_raw_per_rpm * 1000.0f +
                          speed_ff_slope_raw_per_rpm * 0.55f *
                          (magnitude - 1000.0f);
    }
    magnitude = friction + feedforward_rpm;
    /* The 200 rpm tests need about 20 raw less current while 500/1000 rpm
       already track well.  Fade this identified trim out by 500 rpm. */
    low_speed_trim = 20.0f * (1.0f - Control_Clamp(fabsf(target_rpm) /
                                                   500.0f, 1.0f));
    magnitude -= low_speed_trim;
    if (magnitude < 0.0f) magnitude = 0.0f;
    return target_rpm > 0.0f ? magnitude : -magnitude;
#else
    (void)target_rpm;
    return 0.0f;
#endif
}

void Control_Init(void)
{
    /* Safe initial gains; tune speed loop first, then position loop. */
#if APP_MOTOR_TYPE == MOTOR_TYPE_M2006
    speed_ff_static_raw = 0.0f;
    speed_ff_slope_raw_per_rpm = 0.0f;
    PID_Init(&g_speed_pid, 0.8f, 1.5f, 0.0f, 1500.0f,
             (float)APP_CURRENT_SAFE_LIMIT);
#else
    speed_ff_static_raw = 200.0f;
    speed_ff_slope_raw_per_rpm = 0.30f;
    /* With identified speed feedforward, PI only corrects the residual. */
    PID_Init(&g_speed_pid, 1.15f, 0.35f, 0.0f, 1500.0f,
             (float)APP_CURRENT_SAFE_LIMIT);
#endif
    /* Position tuning resumes only after the speed loop is identified. */
    PID_Init(&g_position_pid, 36.0f, 0.0f, 0.0f, 0.0f,
             APP_POSITION_SPEED_LIMIT_RPM);
#if APP_MOTOR_TYPE == MOTOR_TYPE_M2006
    Control_SetSTSMCGains(45.0f, 400.0f, 30.0f);
#else
    Control_SetSTSMCGains(60.0f, 500.0f, 30.0f);
#endif
    Control_SetNFTSMCGains(8.0f, 20.0f, 2.0f);
    control_mode = CONTROL_STOP;
    control_enabled = 0U;
    position_guard_tripped = 0U;
    position_hold_active = 0U;
    position_guard_ticks = 0U;
    speed_feedback_filtered_rpm = 0.0f;
    speed_reference_rpm = 0.0f;
    speed_filter_valid = 0U;
}

void Control_Update1ms(void)
{
    Motor_Feedback_t fb;
    float requested_speed = 0.0f;

    Motor_UpdateOnline(HAL_GetTick());
    Motor_GetFeedback(&fb);

    /* Keep telemetry raw, but do not let the controller chase C620 low-speed
       sample ripple. Alpha 0.05 at 1 kHz is approximately a 20 ms filter. */
    if (!speed_filter_valid) {
        speed_feedback_filtered_rpm = (float)fb.speed_rpm;
        speed_filter_valid = 1U;
    } else {
        speed_feedback_filtered_rpm += 0.05f *
            ((float)fb.speed_rpm - speed_feedback_filtered_rpm);
    }

    if (!control_enabled || control_mode == CONTROL_STOP || !fb.online ||
        (APP_HAS_TEMPERATURE && fb.temperature_c >= APP_OVER_TEMPERATURE_C)) {
        current_command = 0;
        speed_command_rpm = 0.0f;
        speed_reference_rpm = 0.0f;
        PID_Reset(&g_speed_pid);
        Control_ResetSMC();
        if (!control_enabled || control_mode == CONTROL_STOP) PID_Reset(&g_position_pid);
        (void)Motor_SendCurrent(0);
        return;
    }

    if (control_mode == CONTROL_POSITION) {
        float position_error = target_position_deg - fb.output_angle_deg;
        float absolute_error = fabsf(position_error);
        if (absolute_error <= APP_POSITION_DEADBAND_DEG) {
            position_hold_active = 1U;
        } else if (position_hold_active &&
                   absolute_error >= APP_POSITION_DEADBAND_EXIT_DEG) {
            position_hold_active = 0U;
        }
        if (position_hold_active) {
            requested_speed = 0.0f;
            speed_reference_rpm = 0.0f;
            PID_Reset(&g_position_pid);
            Control_ResetSMC();
        } else if (control_algorithm == CONTROL_ALGORITHM_SMC) {
            requested_speed = Control_NFTSMCPositionUpdate(target_position_deg, &fb);
        } else {
            requested_speed = PID_Update(&g_position_pid, target_position_deg,
                                         fb.output_angle_deg, APP_CONTROL_PERIOD_S);
        }
        requested_speed = Control_Clamp(requested_speed,
                                        APP_POSITION_SPEED_LIMIT_RPM);
        if (!position_hold_active) {
            float position_slew_rpm_per_s =
                (fabsf(requested_speed) <= fabsf(speed_reference_rpm)) ?
                APP_POSITION_DECEL_RPM_PER_S : APP_POSITION_ACCEL_RPM_PER_S;
            speed_reference_rpm = Control_Approach(
                speed_reference_rpm, requested_speed,
                position_slew_rpm_per_s * APP_CONTROL_PERIOD_S);
            requested_speed = speed_reference_rpm;
        }

        if (position_guard_ticks == 0U) position_guard_start_error = absolute_error;
        position_guard_ticks++;
        if (position_guard_ticks >= APP_POSITION_GUARD_TIME_MS) {
            if (absolute_error > 10.0f &&
                absolute_error > position_guard_start_error +
                                 APP_POSITION_GUARD_GROWTH_DEG) {
                position_guard_tripped = 1U;
                control_enabled = 0U;
                current_command = 0;
                speed_command_rpm = 0.0f;
                PID_Reset(&g_speed_pid);
                PID_Reset(&g_position_pid);
                Control_ResetSMC();
                (void)Motor_SendCurrent(0);
                return;
            }
            position_guard_ticks = 0U;
        }
    } else {
        /* Limit reference acceleration in direct speed mode.  This avoids the
           simultaneous proportional and feedforward current step that made
           the unloaded 200 rpm response sensitive to breakaway friction. */
        /* The 800/1200 rpm/s profile was safe but made the high-speed and
           reversal tests spend most of each stage inside the reference ramp.
           The latest four motor/ESC combinations have ample current margin
           and little overshoot, so accelerate moderately faster and use a
           stronger deceleration/reversal rate. */
        float slew_rpm_per_s = (fabsf(target_speed_rpm) <=
                                fabsf(speed_reference_rpm)) ? 1800.0f : 1200.0f;
        speed_reference_rpm = Control_Approach(
            speed_reference_rpm, target_speed_rpm,
            slew_rpm_per_s * APP_CONTROL_PERIOD_S);
        requested_speed = speed_reference_rpm;
    }
    requested_speed = Control_Clamp(requested_speed, APP_SPEED_TARGET_LIMIT_RPM);
    speed_command_rpm = requested_speed;
    if (control_algorithm == CONTROL_ALGORITHM_SMC) {
        current_command = (int16_t)Control_STSMCUpdate(requested_speed,
                                                       speed_feedback_filtered_rpm);
    } else {
        /* Static feedforward already supplies the breakaway current.  Do not
           integrate the large standstill error; enable I only near target. */
        float integral_band = 50.0f;
        float feedback_current;
        /* During a commanded speed ramp, proportional feedback and
           feedforward provide the acceleration; freeze I to avoid storing a
           transient error.  Once the ramp reaches its final target, widen
           the I-zone completely so motor/ESC combinations with a larger
           model mismatch can remove their steady-state error. Position keeps
           the original narrow band because its inner-loop target is moving. */
        if (control_mode == CONTROL_SPEED) {
            integral_band = (fabsf(target_speed_rpm - speed_reference_rpm) <=
                             1.0f) ? 0.0f : 1.0f;
        }
        feedback_current = PID_UpdateWithIntegralBand(
                                            &g_speed_pid, requested_speed,
                                            speed_feedback_filtered_rpm,
                                            APP_CONTROL_PERIOD_S,
                                            integral_band);
        float total_current = feedback_current +
                              Control_SpeedFeedforward(requested_speed);
        current_command = (int16_t)Control_Clamp(total_current,
                                                 (float)APP_CURRENT_SAFE_LIMIT);
    }
    (void)Motor_SendCurrent(current_command);
}

void Control_SetEnabled(uint8_t enabled)
{
    control_enabled = enabled ? 1U : 0U;
    if (control_enabled) {
        position_guard_tripped = 0U;
        position_hold_active = 0U;
        position_guard_ticks = 0U;
    }
    if (!control_enabled) {
        current_command = 0;
        speed_reference_rpm = 0.0f;
        PID_Reset(&g_speed_pid);
        PID_Reset(&g_position_pid);
        Control_ResetSMC();
    }
}

void Control_SetMode(Control_Mode_t mode)
{
    Motor_Feedback_t fb;
    if (mode != control_mode) {
        control_enabled = 0U;
        current_command = 0;
        (void)Motor_SendCurrent(0);
    }
    control_mode = mode;
    speed_reference_rpm = 0.0f;
    if (mode == CONTROL_POSITION) {
        Motor_GetFeedback(&fb);
        target_position_deg = fb.output_angle_deg;
    }
    position_guard_tripped = 0U;
    position_hold_active = 0U;
    position_guard_ticks = 0U;
    PID_Reset(&g_speed_pid);
    PID_Reset(&g_position_pid);
    Control_ResetSMC();
}

void Control_SetAlgorithm(Control_Algorithm_t algorithm)
{
    control_algorithm = (algorithm == CONTROL_ALGORITHM_SMC) ?
                        CONTROL_ALGORITHM_SMC : CONTROL_ALGORITHM_PID;
    /* Algorithm changes are intentionally bumpless only after a fresh RUN. */
    control_enabled = 0U;
    current_command = 0;
    speed_reference_rpm = 0.0f;
    PID_Reset(&g_speed_pid);
    PID_Reset(&g_position_pid);
    Control_ResetSMC();
    (void)Motor_SendCurrent(0);
}

void Control_SetSTSMCGains(float k1, float k2, float boundary)
{
    if (k1 < 0.0f || k2 < 0.0f || boundary < 0.1f) return;
    g_speed_stsmc.k1 = k1;
    g_speed_stsmc.k2 = k2;
    g_speed_stsmc.boundary = boundary;
    Control_ResetSMC();
}

void Control_SetNFTSMCGains(float beta, float reaching_gain, float boundary)
{
    if (beta < 0.0f || reaching_gain < 0.0f || boundary < 0.1f) return;
    g_position_nftsmc.beta = beta;
    g_position_nftsmc.reaching_gain = reaching_gain;
    g_position_nftsmc.boundary = boundary;
    g_position_nftsmc.surface = 0.0f;
    g_position_nftsmc.output_rpm = 0.0f;
}

void Control_SetSpeedFeedforward(float static_raw, float slope_raw_per_rpm)
{
    if (static_raw < 0.0f || static_raw > (float)APP_CURRENT_SAFE_LIMIT ||
        slope_raw_per_rpm < 0.0f || slope_raw_per_rpm > 5.0f) return;
    speed_ff_static_raw = static_raw;
    speed_ff_slope_raw_per_rpm = slope_raw_per_rpm;
    PID_Reset(&g_speed_pid);
}

void Control_SetTargetSpeed(float rpm)
{
    float next = Control_Clamp(rpm, APP_SPEED_TARGET_LIMIT_RPM);
    if (fabsf(next - target_speed_rpm) >= 1.0f) PID_Reset(&g_speed_pid);
    target_speed_rpm = next;
}
void Control_SetTargetPosition(float degrees)
{
    float next = Control_Clamp(degrees, APP_POSITION_TARGET_LIMIT_DEG);
    if (fabsf(next - target_position_deg) >= 0.1f) {
        PID_Reset(&g_position_pid);
        PID_Reset(&g_speed_pid);
    }
    target_position_deg = next;
    position_guard_tripped = 0U;
    position_hold_active = 0U;
    position_guard_ticks = 0U;
}

void Control_GetStatus(Control_Status_t *status)
{
    uint32_t primask;
    if (status == 0) return;
    Motor_GetFeedback(&status->motor);
    primask = __get_PRIMASK();
    __disable_irq();
    status->mode = control_mode;
    status->algorithm = control_algorithm;
    status->target_speed_rpm = target_speed_rpm;
    status->target_position_deg = target_position_deg;
    status->speed_command_rpm = speed_command_rpm;
    status->speed_pid_output = g_speed_pid.output;
    status->position_pid_output = g_position_pid.output;
    status->controller_output = (control_algorithm == CONTROL_ALGORITHM_SMC) ?
                                g_speed_stsmc.output : g_speed_pid.output;
    status->sliding_surface = (control_algorithm == CONTROL_ALGORITHM_SMC) ?
                              g_speed_stsmc.surface : 0.0f;
    status->current_command = current_command;
    status->enabled = control_enabled;
    status->position_guard_tripped = position_guard_tripped;
    if (primask == 0U) __enable_irq();
}
