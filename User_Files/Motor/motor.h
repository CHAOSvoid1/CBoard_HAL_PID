#ifndef APP_MOTOR_H
#define APP_MOTOR_H

#include "main.h"
#include "app_config.h"
#include <stdint.h>

typedef struct {
    uint16_t encoder;
    int16_t speed_rpm;
    int16_t current_raw;
    uint8_t temperature_c;
    float rotor_angle_deg;
    float output_angle_deg;
    uint32_t rx_count;
    uint32_t bus_rx_count;
    uint32_t last_std_id;
    uint32_t last_rx_ms;
    uint8_t online;
} Motor_Feedback_t;

void Motor_CAN_Init(void);
void Motor_CAN_RxCallback(CAN_HandleTypeDef *hcan);
HAL_StatusTypeDef Motor_SendCurrent(int16_t current_raw);
void Motor_UpdateOnline(uint32_t now_ms);
void Motor_ZeroPosition(void);
void Motor_GetFeedback(Motor_Feedback_t *out);
uint32_t Motor_GetTxOkCount(void);
uint32_t Motor_GetTxFailCount(void);
uint32_t Motor_GetCanError(void);
uint32_t Motor_GetBusRxCount(void);
uint32_t Motor_GetLastStdId(void);

#endif
