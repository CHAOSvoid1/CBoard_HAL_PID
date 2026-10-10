#ifndef APP_CONTROL_H
#define APP_CONTROL_H

#include "motor.h"
#include "pid.h"
#include <stdint.h>

typedef enum {
    CONTROL_STOP = 0,
    CONTROL_SPEED,
    CONTROL_POSITION
} Control_Mode_t;

typedef enum {
    CONTROL_ALGORITHM_PID = 0,
    CONTROL_ALGORITHM_SMC
} Control_Algorithm_t;

typedef struct {
    float k1;
    float k2;
    float boundary;
    float integral;
    float surface;
    float output;
} STSMC_t;

typedef struct {
    float beta;
    float reaching_gain;
    float boundary;
    float surface;
    float output_rpm;
} NFTSMC_t;

typedef struct {
    Motor_Feedback_t motor;
    Control_Mode_t mode;
    Control_Algorithm_t algorithm;
    float target_speed_rpm;
    float target_position_deg;
    float speed_command_rpm;
    float speed_pid_output;
    float position_pid_output;
    float controller_output;
    float sliding_surface;
    int16_t current_command;
    uint8_t enabled;
    uint8_t position_guard_tripped;
} Control_Status_t;

extern PID_t g_speed_pid;
extern PID_t g_position_pid;
extern STSMC_t g_speed_stsmc;
extern NFTSMC_t g_position_nftsmc;

void Control_Init(void);
void Control_Update1ms(void);
void Control_SetEnabled(uint8_t enabled);
void Control_SetMode(Control_Mode_t mode);
void Control_SetAlgorithm(Control_Algorithm_t algorithm);
void Control_SetSTSMCGains(float k1, float k2, float boundary);
void Control_SetNFTSMCGains(float beta, float reaching_gain, float boundary);
void Control_SetSpeedFeedforward(float static_raw, float slope_raw_per_rpm);
void Control_SetTargetSpeed(float rpm);
void Control_SetTargetPosition(float degrees);
void Control_GetStatus(Control_Status_t *status);

#endif
