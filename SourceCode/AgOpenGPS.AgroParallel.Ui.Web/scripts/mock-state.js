window.agpMockState = {
  timestamp: new Date().toISOString(),
  gps: {
    isPositionInitialized: true,
    fixQuality: "RTK FIJO",
    ageSeconds: 1.2,
    satellites: 23,
    accuracyCm: 2,
    latitude: -34.6037,
    longitude: -58.3816
  },
  machine: {
    speedKph: 7.2,
    headingDegrees: 74,
    rollDegrees: -1.8,
    isReverseDetected: false
  },
  guidance: {
    mode: "Stanley",
    currentLineName: "A12",
    crossTrackErrorCm: 2.5,
    steerSetpointDegrees: 3.4,
    actualSteerDegrees: 3.1,
    isAutoSteerOn: true,
    isAutoSteerAvailable: true,
    isUturnAvailable: true,
    isUturnOn: false
  },
  field: {
    isJobStarted: true,
    fieldName: "Lote Norte",
    workedHa: 9.14,
    actualWorkedHa: 8.92,
    coveragePercent: 68,
    isOutOfBounds: false
  },
  sections: {
    total: 16,
    active: 16,
    mode: "Auto"
  },
  connectivity: {
    isCoreConnected: true,
    isAgIoConnected: true,
    isOrbitXConnected: false,
    hardwareMessage: ""
  },
  alerts: []
};
