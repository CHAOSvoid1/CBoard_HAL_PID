#ifndef APP_VOFA_H
#define APP_VOFA_H

#include "main.h"
#include "control.h"
#include <stdint.h>

typedef enum {
    VOFA_FORMAT_FIREWATER = 0,
    VOFA_FORMAT_JUSTFLOAT
} Vofa_Format_t;

void Vofa_Init(void);
void Vofa_RxCompleteCallback(UART_HandleTypeDef *huart);
void Vofa_ErrorCallback(UART_HandleTypeDef *huart);
void Vofa_ProcessCommands(void);
void Vofa_SendTelemetry(const Control_Status_t *status);
void Vofa_SetFormat(Vofa_Format_t format);
Vofa_Format_t Vofa_GetFormat(void);

#endif
