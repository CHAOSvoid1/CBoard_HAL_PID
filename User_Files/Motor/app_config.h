#ifndef APP_CONFIG_H
#define APP_CONFIG_H

#define MOTOR_TYPE_M2006               2006U
#define MOTOR_TYPE_M3508               3508U

/* ---------------- User configuration ---------------- */
#define APP_MOTOR_TYPE                 MOTOR_TYPE_M3508
#define APP_MOTOR_ID                   4U
#define APP_USE_CAN1                   1U
#define APP_USE_USART6                 0U
#define APP_VOFA_FORMAT_FIREWATER      0U
#define APP_VOFA_FORMAT_JUSTFLOAT      1U
#define APP_DEFAULT_VOFA_FORMAT        APP_VOFA_FORMAT_FIREWATER
#define APP_CONTROLLER_PID             0U
#define APP_CONTROLLER_SMC             1U
#define APP_DEFAULT_CONTROLLER         APP_CONTROLLER_PID
#define APP_CONTROL_PERIOD_S           0.001f
#define APP_TELEMETRY_PERIOD_MS        20U
#define APP_FEEDBACK_TIMEOUT_MS        100U
#define APP_OVER_TEMPERATURE_C         85U
#define APP_POSITION_TARGET_LIMIT_DEG  720.0f
#define APP_POSITION_DEADBAND_DEG      0.30f
#define APP_POSITION_DEADBAND_EXIT_DEG 0.80f
#define APP_POSITION_GUARD_TIME_MS     200U
#define APP_POSITION_GUARD_GROWTH_DEG  5.0f

/* Conservative first-run limits. Increase only after verifying feedback. */
#if APP_MOTOR_TYPE == MOTOR_TYPE_M2006
#define APP_SPEED_TARGET_LIMIT_RPM     3000.0f
#define APP_POSITION_SPEED_LIMIT_RPM   1500.0f
#define APP_POSITION_ACCEL_RPM_PER_S   2400.0f
#define APP_POSITION_DECEL_RPM_PER_S   3600.0f
#define APP_GEAR_RATIO                 36.0f
#define APP_CURRENT_FULL_SCALE         10000
#define APP_CURRENT_SAFE_LIMIT         3000
#define APP_HAS_TEMPERATURE            0U
#elif APP_MOTOR_TYPE == MOTOR_TYPE_M3508
#define APP_SPEED_TARGET_LIMIT_RPM     7400.0f
#define APP_POSITION_SPEED_LIMIT_RPM   3000.0f
#define APP_POSITION_ACCEL_RPM_PER_S   4000.0f
#define APP_POSITION_DECEL_RPM_PER_S   6000.0f
#define APP_GEAR_RATIO                 (3591.0f / 187.0f)
#define APP_CURRENT_FULL_SCALE         16384
#define APP_CURRENT_SAFE_LIMIT         4000
#define APP_HAS_TEMPERATURE            1U
#else
#error "APP_MOTOR_TYPE must be MOTOR_TYPE_M2006 or MOTOR_TYPE_M3508"
#endif

#if (APP_MOTOR_ID < 1U) || (APP_MOTOR_ID > 8U)
#error "APP_MOTOR_ID must be 1..8"
#endif

#endif
