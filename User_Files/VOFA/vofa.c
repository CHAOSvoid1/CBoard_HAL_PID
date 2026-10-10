#include "vofa.h"
#include "app_config.h"
#include <stdlib.h>
#include <string.h>
#include <stdio.h>

extern UART_HandleTypeDef huart1;
extern UART_HandleTypeDef huart6;

#define RX_RING_SIZE 128U
#define COMMAND_SIZE 96U

static volatile uint8_t rx_ring[RX_RING_SIZE];
static volatile uint16_t rx_write;
static volatile uint16_t rx_read;
static uint8_t rx_byte1;
static uint8_t rx_byte6;
static UART_HandleTypeDef *active_uart;
static char command_buffer[COMMAND_SIZE];
static uint16_t command_length;
#if APP_DEFAULT_VOFA_FORMAT == APP_VOFA_FORMAT_JUSTFLOAT
static volatile Vofa_Format_t vofa_format = VOFA_FORMAT_JUSTFLOAT;
#else
static volatile Vofa_Format_t vofa_format = VOFA_FORMAT_FIREWATER;
#endif

static UART_HandleTypeDef *Vofa_DefaultUART(void)
{
#if APP_USE_USART6
    return &huart6;
#else
    return &huart1;
#endif
}

static void Vofa_Send(const uint8_t *data, uint16_t length)
{
    UART_HandleTypeDef *uart = active_uart != 0 ? active_uart : Vofa_DefaultUART();
    (void)HAL_UART_Transmit(uart, (uint8_t *)data, length, 100U);
}

static void Vofa_SendOn(UART_HandleTypeDef *uart, const char *text)
{
    if (uart != 0 && text != 0) {
        (void)HAL_UART_Transmit(uart, (uint8_t *)text, (uint16_t)strlen(text), 100U);
    }
}

static void Vofa_SendText(const char *text)
{
    if (text != 0) Vofa_Send((const uint8_t *)text, (uint16_t)strlen(text));
}

static long FloatToMilli(float value)
{
    return (long)(value * 1000.0f);
}

static void AppendChar(char *buffer, uint16_t size, uint16_t *used, char value)
{
    if (*used < size) buffer[(*used)++] = value;
}

static void AppendText(char *buffer, uint16_t size, uint16_t *used, const char *text)
{
    while (*text != '\0' && *used < size) buffer[(*used)++] = *text++;
}

static void AppendUnsigned(char *buffer, uint16_t size, uint16_t *used,
                           unsigned long value)
{
    char digits[12];
    uint8_t count = 0U;
    do {
        digits[count++] = (char)('0' + value % 10UL);
        value /= 10UL;
    } while (value != 0UL && count < sizeof(digits));
    while (count > 0U) AppendChar(buffer, size, used, digits[--count]);
}

static void AppendSigned(char *buffer, uint16_t size, uint16_t *used, long value)
{
    unsigned long magnitude;
    if (value < 0L) {
        AppendChar(buffer, size, used, '-');
        magnitude = (unsigned long)(-(value + 1L)) + 1UL;
    } else {
        magnitude = (unsigned long)value;
    }
    AppendUnsigned(buffer, size, used, magnitude);
}

static void AppendMilli(char *buffer, uint16_t size, uint16_t *used, float value)
{
    long scaled = FloatToMilli(value);
    unsigned long magnitude = (scaled < 0) ? (unsigned long)(-scaled) : (unsigned long)scaled;
    unsigned long fraction = magnitude % 1000UL;
    if (scaled < 0) AppendChar(buffer, size, used, '-');
    AppendUnsigned(buffer, size, used, magnitude / 1000UL);
    AppendChar(buffer, size, used, '.');
    AppendChar(buffer, size, used, (char)('0' + fraction / 100UL));
    AppendChar(buffer, size, used, (char)('0' + (fraction / 10UL) % 10UL));
    AppendChar(buffer, size, used, (char)('0' + fraction % 10UL));
}

static void Vofa_PrintHelp(void)
{
    Vofa_SendText("\r\nCBoard HAL M2006/M3508 PID / sliding-mode control\r\n");
    Vofa_SendText("RUN | STOP | ZERO\r\n");
    Vofa_SendText("PING | STATUS (UART/CAN diagnostic)\r\n");
    Vofa_SendText("MODE:SPEED | MODE:POSITION\r\n");
    Vofa_SendText("SPEED:1000 | POS:90\r\n");
    Vofa_SendText("PID:SPEED,1.0,2.0,0.0\r\n");
    Vofa_SendText("PID:POSITION,8.0,0.0,0.05\r\n");
    Vofa_SendText("FF:SPEED,300,0.60\r\n");
    Vofa_SendText("CTRL:PID | CTRL:SMC (switching forces STOP)\r\n");
    Vofa_SendText("SMC:SPEED,60,500,30\r\n");
    Vofa_SendText("SMC:POSITION,8,20,2\r\n");
    Vofa_SendText("FORMAT:FIREWATER | FORMAT:JUSTFLOAT\r\n");
}

static void Vofa_HandleCommand(char *command)
{
    float kp, ki, kd;
    if (strcmp(command, "PING") == 0) {
        Vofa_SendText("event:pong_uart_ok\r\n");
    } else if (strcmp(command, "STATUS") == 0) {
        Control_Status_t status;
        char line[256];
        Control_GetStatus(&status);
        (void)snprintf(line, sizeof(line),
                       "diag:uart=USART%u,can_online=%u,target_rx=%lu,bus_rx=%lu,last_id=0x%03lX,can_tx_ok=%lu,can_tx_fail=%lu,can_esr=0x%08lX,mode=%u,enabled=%u,pos_guard=%u,controller=%s\r\n",
                       active_uart == &huart6 ? 6U : 1U,
                       (unsigned int)status.motor.online,
                       (unsigned long)status.motor.rx_count,
                       (unsigned long)Motor_GetBusRxCount(),
                       (unsigned long)Motor_GetLastStdId(),
                       (unsigned long)Motor_GetTxOkCount(),
                       (unsigned long)Motor_GetTxFailCount(),
                       (unsigned long)Motor_GetCanError(),
                       (unsigned int)status.mode,
                       (unsigned int)status.enabled,
                       (unsigned int)status.position_guard_tripped,
                       status.algorithm == CONTROL_ALGORITHM_SMC ? "SMC" : "PID");
        Vofa_SendText(line);
    } else if (strcmp(command, "RUN") == 0) {
        Control_SetEnabled(1U);
        Vofa_SendText("event:run\r\n");
    } else if (strcmp(command, "STOP") == 0) {
        Control_SetEnabled(0U);
        Vofa_SendText("event:stop\r\n");
    } else if (strcmp(command, "ZERO") == 0) {
        Motor_ZeroPosition();
        Vofa_SendText("event:zero_position\r\n");
    } else if (strcmp(command, "MODE:SPEED") == 0) {
        Control_SetMode(CONTROL_SPEED);
        Vofa_SendText("event:mode_speed\r\n");
    } else if (strcmp(command, "MODE:POSITION") == 0) {
        Control_SetMode(CONTROL_POSITION);
        Vofa_SendText("event:mode_position\r\n");
    } else if (strncmp(command, "SPEED:", 6) == 0) {
        Control_SetTargetSpeed((float)atof(command + 6));
        Vofa_SendText("event:set_speed\r\n");
    } else if (strncmp(command, "POS:", 4) == 0) {
        Control_SetTargetPosition((float)atof(command + 4));
        Vofa_SendText("event:set_position\r\n");
    } else if (sscanf(command, "PID:SPEED,%f,%f,%f", &kp, &ki, &kd) == 3) {
        PID_SetGains(&g_speed_pid, kp, ki, kd);
        Vofa_SendText("event:set_speed_pid\r\n");
    } else if (sscanf(command, "PID:POSITION,%f,%f,%f", &kp, &ki, &kd) == 3) {
        PID_SetGains(&g_position_pid, kp, ki, kd);
        Vofa_SendText("event:set_position_pid\r\n");
    } else if (sscanf(command, "FF:SPEED,%f,%f", &kp, &ki) == 2) {
        Control_SetSpeedFeedforward(kp, ki);
        Vofa_SendText("event:set_speed_feedforward\r\n");
    } else if (strcmp(command, "CTRL:PID") == 0) {
        Control_SetAlgorithm(CONTROL_ALGORITHM_PID);
        Vofa_SendText("event:controller_pid_stopped\r\n");
    } else if (strcmp(command, "CTRL:SMC") == 0) {
        Control_SetAlgorithm(CONTROL_ALGORITHM_SMC);
        Vofa_SendText("event:controller_smc_stopped\r\n");
    } else if (sscanf(command, "SMC:SPEED,%f,%f,%f", &kp, &ki, &kd) == 3) {
        Control_SetSTSMCGains(kp, ki, kd);
        Vofa_SendText("event:set_speed_stsmc\r\n");
    } else if (sscanf(command, "SMC:POSITION,%f,%f,%f", &kp, &ki, &kd) == 3) {
        Control_SetNFTSMCGains(kp, ki, kd);
        Vofa_SendText("event:set_position_nftsmc\r\n");
    } else if (strcmp(command, "FORMAT:FIREWATER") == 0) {
        Vofa_SetFormat(VOFA_FORMAT_FIREWATER);
        Vofa_SendText("event:format_firewater\r\n");
    } else if (strcmp(command, "FORMAT:JUSTFLOAT") == 0) {
        Vofa_SendText("event:format_justfloat\r\n");
        Vofa_SetFormat(VOFA_FORMAT_JUSTFLOAT);
    } else if (strcmp(command, "HELP") == 0 || strcmp(command, "?") == 0) {
        Vofa_PrintHelp();
    } else if (command[0] != '\0') {
        Vofa_SendText("event:unknown_command\r\n");
    }
}

void Vofa_Init(void)
{
    rx_write = 0U;
    rx_read = 0U;
    command_length = 0U;
    active_uart = Vofa_DefaultUART();
    if (HAL_UART_Receive_IT(&huart1, &rx_byte1, 1U) != HAL_OK) Error_Handler();
    if (HAL_UART_Receive_IT(&huart6, &rx_byte6, 1U) != HAL_OK) Error_Handler();
    Vofa_SendOn(&huart1, "\r\nboot:CBoard UART auto-detect USART1 ready\r\n");
    Vofa_SendOn(&huart6, "\r\nboot:CBoard UART auto-detect USART6 ready\r\n");
    if (vofa_format == VOFA_FORMAT_FIREWATER) Vofa_PrintHelp();
}

void Vofa_RxCompleteCallback(UART_HandleTypeDef *huart)
{
    uint16_t next;
    uint8_t byte;
    if (huart == &huart1) byte = rx_byte1;
    else if (huart == &huart6) byte = rx_byte6;
    else return;
    active_uart = huart;
    next = (uint16_t)((rx_write + 1U) % RX_RING_SIZE);
    if (next != rx_read) {
        rx_ring[rx_write] = byte;
        rx_write = next;
    }
    if (huart == &huart1) (void)HAL_UART_Receive_IT(&huart1, &rx_byte1, 1U);
    else (void)HAL_UART_Receive_IT(&huart6, &rx_byte6, 1U);
}

void Vofa_ErrorCallback(UART_HandleTypeDef *huart)
{
    if (huart != &huart1 && huart != &huart6) return;
    __HAL_UART_CLEAR_OREFLAG(huart);
    if (huart == &huart1) (void)HAL_UART_Receive_IT(&huart1, &rx_byte1, 1U);
    else (void)HAL_UART_Receive_IT(&huart6, &rx_byte6, 1U);
}

void Vofa_ProcessCommands(void)
{
    uint8_t byte;
    while (rx_read != rx_write) {
        byte = rx_ring[rx_read];
        rx_read = (uint16_t)((rx_read + 1U) % RX_RING_SIZE);
        if (byte == '\r' || byte == '\n') {
            if (command_length > 0U) {
                command_buffer[command_length] = '\0';
                Vofa_HandleCommand(command_buffer);
                command_length = 0U;
            }
        } else if (command_length < COMMAND_SIZE - 1U) {
            command_buffer[command_length++] = (char)byte;
        } else {
            command_length = 0U;
        }
    }
}

void Vofa_SendTelemetry(const Control_Status_t *status)
{
    if (status == 0) return;
    if (vofa_format == VOFA_FORMAT_JUSTFLOAT) {
        struct {
            float channel[8];
            uint8_t tail[4];
        } frame;
        /* In speed mode this equals the requested target.  In position mode
           it exposes the position loop's live speed request instead of the
           stale target left by the previous speed test. */
        frame.channel[0] = status->speed_command_rpm;
        frame.channel[1] = (float)status->motor.speed_rpm;
        frame.channel[2] = status->target_position_deg;
        frame.channel[3] = status->motor.output_angle_deg;
        frame.channel[4] = (float)status->current_command;
        frame.channel[5] = (float)status->motor.temperature_c;
        frame.channel[6] = (float)status->motor.online;
        frame.channel[7] = status->controller_output;
        frame.tail[0] = 0x00U;
        frame.tail[1] = 0x00U;
        frame.tail[2] = 0x80U;
        frame.tail[3] = 0x7FU;
        Vofa_Send((const uint8_t *)&frame, (uint16_t)sizeof(frame));
    } else {
        char line[192];
        uint16_t used = 0U;
        AppendText(line, sizeof(line), &used, "motor:");
        AppendMilli(line, sizeof(line), &used, status->speed_command_rpm);
        AppendChar(line, sizeof(line), &used, ',');
        AppendMilli(line, sizeof(line), &used, (float)status->motor.speed_rpm);
        AppendChar(line, sizeof(line), &used, ',');
        AppendMilli(line, sizeof(line), &used, status->target_position_deg);
        AppendChar(line, sizeof(line), &used, ',');
        AppendMilli(line, sizeof(line), &used, status->motor.output_angle_deg);
        AppendChar(line, sizeof(line), &used, ',');
        AppendSigned(line, sizeof(line), &used, status->current_command);
        AppendChar(line, sizeof(line), &used, ',');
        AppendUnsigned(line, sizeof(line), &used, status->motor.temperature_c);
        AppendChar(line, sizeof(line), &used, ',');
        AppendUnsigned(line, sizeof(line), &used, status->motor.online);
        AppendChar(line, sizeof(line), &used, ',');
        AppendUnsigned(line, sizeof(line), &used, status->motor.rx_count);
        AppendChar(line, sizeof(line), &used, '\n');
        Vofa_Send((const uint8_t *)line, used);
    }
}

void Vofa_SetFormat(Vofa_Format_t format) { vofa_format = format; }
Vofa_Format_t Vofa_GetFormat(void) { return vofa_format; }
