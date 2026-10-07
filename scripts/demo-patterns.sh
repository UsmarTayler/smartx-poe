#!/usr/bin/env bash
# Drives the pattern analysis example by hand against a running stack, so it can be watched learning.
#
#   "If a user frequently opens Pump A right after Moisture Sensor B drops below its low level"
#
# Rack Row 1's temperature sensor stands in for the moisture sensor and the CRAC Water Pump for Pump A, because both are in
# the demo fleet. The sensor is registered for 18 to 32, so its low watch level is 20.8 and it is re-armed above 21.5.
#
# Start the stack with a short window so that this takes minutes and not a shift:
#
#   SMARTX_PATTERN_WINDOW=00:00:30 docker compose up -d --build
#
# Then, with curl and node on the path (Git Bash on Windows has both):
#
#   scripts/demo-patterns.sh            # five rounds, 90 seconds apart, about eight minutes
#   API=http://localhost:5266/api ROUNDS=4 scripts/demo-patterns.sh
#
# Each round: the sensor drops below its low level (and drops a little more, which is the same dip and is not a new crossing),
# the operator opens the pump three times (which is one response and not three), and the sensor recovers past its re-arm
# level. A second device, the Dock Leveller Pump, is opened every ten seconds throughout: it follows every trigger, so its
# confidence is as high as the pump's, and its lift shows it tells us nothing.
set -u

API=${API:-http://localhost:5266/api}
ROUNDS=${ROUNDS:-5}
SENSOR=${SENSOR:-B8:27:EB:10:00:01}
PUMP=${PUMP:-B8:27:EB:10:00:06}
BUSY=${BUSY:-B8:27:EB:10:00:1E}
JSON='Content-Type: application/json'

post() { curl -s -o /dev/null -w '%{http_code}' -X POST "$API/$1" -H "$JSON" -d "$2"; }
reading() { post telemetry "{\"macAddress\":\"$SENSOR\",\"value\":$1}"; }
open() { post activity/selection "{\"macAddress\":\"$1\"}"; }

summarise() {
  node -e '
    let text = "";
    process.stdin.on("data", chunk => text += chunk).on("end", () => {
      const rows = JSON.parse(text).associations.map(a => ({
        trigger: `${a.trigger.kind}/${a.trigger.detail} @ ${a.trigger.scopeLabel}`,
        action: `${a.action.kind} ${a.action.targetLabel}`,
        level: a.level,
        support: +a.support.toFixed(2),
        confidence: +a.confidence.toFixed(2),
        lift: +a.lift.toFixed(2),
        qualified: a.qualified,
      }));
      console.table(rows);
    });'
}

echo "Letting the fleet settle for 25 seconds."
sleep 25

( while true; do open "$BUSY" > /dev/null; sleep 10; done ) &
BUSY_LOOP=$!
trap 'kill $BUSY_LOOP 2>/dev/null' EXIT

for round in $(seq 1 "$ROUNDS"); do
  echo "Round $round: the sensor drops below its low level ($(reading 19.5)), dips a little more ($(reading 19.2))."
  sleep 5
  echo "          the operator opens the pump three times ($(open "$PUMP") $(open "$PUMP") $(open "$PUMP"))."
  echo "          the sensor recovers past its re-arm level ($(reading 26))."
  sleep 80
done

echo
echo "What was learned about the sensor, including what did not qualify:"
curl -s "$API/patterns?mac=$SENSOR&includeWeak=true" | summarise
echo
echo "What qualifies anywhere in the mesh:"
curl -s "$API/patterns?limit=500" | summarise
