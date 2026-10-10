#ifndef APP_PID_H
#define APP_PID_H

typedef struct {
    float kp;
    float ki;
    float kd;
    float integral;
    float previous_error;
    float integral_limit;
    float output_limit;
    float output;
} PID_t;

void PID_Init(PID_t *pid, float kp, float ki, float kd,
              float integral_limit, float output_limit);
void PID_Reset(PID_t *pid);
void PID_SetGains(PID_t *pid, float kp, float ki, float kd);
float PID_Update(PID_t *pid, float target, float measurement, float dt);
float PID_UpdateWithIntegralBand(PID_t *pid, float target, float measurement,
                                 float dt, float integral_band);

#endif
