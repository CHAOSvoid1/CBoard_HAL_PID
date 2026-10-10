#include "pid.h"

static float PID_Clamp(float value, float limit)
{
    if (value > limit) return limit;
    if (value < -limit) return -limit;
    return value;
}

void PID_Init(PID_t *pid, float kp, float ki, float kd,
              float integral_limit, float output_limit)
{
    pid->kp = kp;
    pid->ki = ki;
    pid->kd = kd;
    pid->integral_limit = integral_limit;
    pid->output_limit = output_limit;
    PID_Reset(pid);
}

void PID_Reset(PID_t *pid)
{
    pid->integral = 0.0f;
    pid->previous_error = 0.0f;
    pid->output = 0.0f;
}

void PID_SetGains(PID_t *pid, float kp, float ki, float kd)
{
    pid->kp = kp;
    pid->ki = ki;
    pid->kd = kd;
    PID_Reset(pid);
}

float PID_UpdateWithIntegralBand(PID_t *pid, float target, float measurement,
                                 float dt, float integral_band)
{
    float error = target - measurement;
    float derivative = (dt > 0.0f) ? (error - pid->previous_error) / dt : 0.0f;
    float absolute_error = error >= 0.0f ? error : -error;
    float candidate_integral = pid->integral;
    if (integral_band <= 0.0f || absolute_error <= integral_band) {
        candidate_integral = PID_Clamp(pid->integral + error * dt,
                                       pid->integral_limit);
    }
    float unsaturated = pid->kp * error + pid->ki * candidate_integral +
                        pid->kd * derivative;

    /* Conditional integration prevents wind-up while the output is saturated. */
    if ((unsaturated < pid->output_limit && unsaturated > -pid->output_limit) ||
        (unsaturated >= pid->output_limit && error < 0.0f) ||
        (unsaturated <= -pid->output_limit && error > 0.0f)) {
        pid->integral = candidate_integral;
    }

    pid->previous_error = error;
    pid->output = PID_Clamp(pid->kp * error + pid->ki * pid->integral +
                            pid->kd * derivative, pid->output_limit);
    return pid->output;
}

float PID_Update(PID_t *pid, float target, float measurement, float dt)
{
    return PID_UpdateWithIntegralBand(pid, target, measurement, dt, 0.0f);
}
