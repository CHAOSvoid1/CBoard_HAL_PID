#include "motor.h"
#include <string.h>

extern CAN_HandleTypeDef hcan1;
extern CAN_HandleTypeDef hcan2;

static volatile Motor_Feedback_t feedback;
static volatile uint16_t last_encoder;
static volatile uint8_t encoder_initialized;
static volatile float zero_offset_deg;
static volatile uint32_t tx_ok_count;
static volatile uint32_t tx_fail_count;

static CAN_HandleTypeDef *Motor_CAN(void)
{
#if APP_USE_CAN1
    return &hcan1;
#else
    return &hcan2;
#endif
}

static int16_t Motor_ClampCurrent(int32_t value)
{
    if (value > APP_CURRENT_SAFE_LIMIT) return APP_CURRENT_SAFE_LIMIT;
    if (value < -APP_CURRENT_SAFE_LIMIT) return -APP_CURRENT_SAFE_LIMIT;
    return (int16_t)value;
}

void Motor_CAN_Init(void)
{
    CAN_FilterTypeDef filter;
    CAN_HandleTypeDef *hcan = Motor_CAN();

    memset((void *)&feedback, 0, sizeof(feedback));
    memset(&filter, 0, sizeof(filter));
    filter.FilterBank = APP_USE_CAN1 ? 0U : 14U;
    filter.FilterMode = CAN_FILTERMODE_IDMASK;
    filter.FilterScale = CAN_FILTERSCALE_32BIT;
    filter.FilterIdHigh = 0U;
    filter.FilterIdLow = 0U;
    filter.FilterMaskIdHigh = 0U;
    filter.FilterMaskIdLow = 0U;
    filter.FilterFIFOAssignment = CAN_RX_FIFO0;
    filter.FilterActivation = ENABLE;
    filter.SlaveStartFilterBank = 14U;

    if (HAL_CAN_ConfigFilter(hcan, &filter) != HAL_OK ||
        HAL_CAN_Start(hcan) != HAL_OK ||
        HAL_CAN_ActivateNotification(hcan, CAN_IT_RX_FIFO0_MSG_PENDING) != HAL_OK) {
        Error_Handler();
    }
}

void Motor_CAN_RxCallback(CAN_HandleTypeDef *hcan)
{
    CAN_RxHeaderTypeDef header;
    uint8_t data[8];
    uint16_t encoder;
    int32_t delta;
    const uint32_t expected_id = 0x200U + APP_MOTOR_ID;

    if (hcan != Motor_CAN()) return;
    while (HAL_CAN_GetRxFifoFillLevel(hcan, CAN_RX_FIFO0) > 0U) {
        if (HAL_CAN_GetRxMessage(hcan, CAN_RX_FIFO0, &header, data) != HAL_OK) break;
        if (header.IDE == CAN_ID_STD && header.RTR == CAN_RTR_DATA) {
            feedback.bus_rx_count++;
            feedback.last_std_id = header.StdId;
        }
        if (header.IDE != CAN_ID_STD || header.RTR != CAN_RTR_DATA ||
            header.DLC < 7U || header.StdId != expected_id) continue;

        encoder = (uint16_t)(((uint16_t)data[0] << 8) | data[1]);
        if (encoder_initialized) {
            delta = (int32_t)encoder - (int32_t)last_encoder;
            if (delta > 4096) delta -= 8192;
            if (delta < -4096) delta += 8192;
            feedback.rotor_angle_deg += (float)delta * (360.0f / 8192.0f);
        } else {
            encoder_initialized = 1U;
        }
        last_encoder = encoder;
        feedback.encoder = encoder;
        feedback.speed_rpm = (int16_t)(((uint16_t)data[2] << 8) | data[3]);
        feedback.current_raw = (int16_t)(((uint16_t)data[4] << 8) | data[5]);
#if APP_HAS_TEMPERATURE
        feedback.temperature_c = data[6];
#else
        feedback.temperature_c = 0U;
#endif
        feedback.output_angle_deg = feedback.rotor_angle_deg / APP_GEAR_RATIO - zero_offset_deg;
        feedback.last_rx_ms = HAL_GetTick();
        feedback.rx_count++;
        feedback.online = 1U;
    }
}

HAL_StatusTypeDef Motor_SendCurrent(int16_t current_raw)
{
    CAN_TxHeaderTypeDef header;
    uint8_t data[8] = {0};
    uint32_t mailbox;
    uint8_t offset = (uint8_t)(((APP_MOTOR_ID - 1U) % 4U) * 2U);
    HAL_StatusTypeDef status;

    memset(&header, 0, sizeof(header));
    current_raw = Motor_ClampCurrent(current_raw);
    header.StdId = (APP_MOTOR_ID <= 4U) ? 0x200U : 0x1FFU;
    header.IDE = CAN_ID_STD;
    header.RTR = CAN_RTR_DATA;
    header.DLC = 8U;
    data[offset] = (uint8_t)((uint16_t)current_raw >> 8);
    data[offset + 1U] = (uint8_t)current_raw;
    status = HAL_CAN_AddTxMessage(Motor_CAN(), &header, data, &mailbox);
    if (status == HAL_OK) tx_ok_count++; else tx_fail_count++;
    return status;
}

void Motor_UpdateOnline(uint32_t now_ms)
{
    feedback.online = (feedback.rx_count > 0U &&
                       (uint32_t)(now_ms - feedback.last_rx_ms) <=
                       APP_FEEDBACK_TIMEOUT_MS) ? 1U : 0U;
}

void Motor_ZeroPosition(void)
{
    uint32_t primask = __get_PRIMASK();
    __disable_irq();
    zero_offset_deg = feedback.rotor_angle_deg / APP_GEAR_RATIO;
    feedback.output_angle_deg = 0.0f;
    if (primask == 0U) __enable_irq();
}

void Motor_GetFeedback(Motor_Feedback_t *out)
{
    uint32_t primask;
    if (out == 0) return;
    primask = __get_PRIMASK();
    __disable_irq();
    *out = feedback;
    if (primask == 0U) __enable_irq();
}

uint32_t Motor_GetTxOkCount(void) { return tx_ok_count; }
uint32_t Motor_GetTxFailCount(void) { return tx_fail_count; }
uint32_t Motor_GetCanError(void) { return Motor_CAN()->Instance->ESR; }
uint32_t Motor_GetBusRxCount(void) { return feedback.bus_rx_count; }
uint32_t Motor_GetLastStdId(void) { return feedback.last_std_id; }
