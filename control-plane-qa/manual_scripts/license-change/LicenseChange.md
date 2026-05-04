# CP-01 License Change Test

**Component:** Control-Plane, Api, Evaluation Server
**Status:** [Draft/Ready/Passed/Failed]

## Description
Validates that a license change message is sent through the control plane to update Redis.

## Preconditions
- [ ] Start the Control-Plane QA deployment with one of the Quickstart powershell scripts
- [ ] If needed, run Start-PortForwards.ps1 (this depends on which quickstart script is used)
- [ ] East and West Clusters Running in Advanced mode with port forwards and host entries
- [ ] `playground` Organization Exists
- [ ] `control-plane-test` Project Exists

## Test Steps
1. **Action:** Connect to redis instances using a gui (Redis Insights, Another Redis Desktop Manager Etc.)
2. **Action:** Navigate to featbit-kafka.west.local and featbit-kafka.east.local in two seperate browsers and a standard and incognito instance.
3. **Action:** Navigate to the featbit.west.local
4. **Action:** Login
5. **Action:** In the featbit ui, update the license.
6. **Action:** Navigate to featbit-main in kafka-ui
7. **Action:** Navigate to topics in kafka-ui under featbit-main
8. **Action:** Click `featbit-control-plane-license-change`
9. **Action:** Click Messages
10. **Action:** Observe a license update message with the new license value
11. **Action:** Observe that the license update exists in redis.west.local
12. **Action:** Observe that the license update exists in redis.east.local

## Expected Results
- Redis in both east and west are updated

## Post-conditions

---
**Notes/Comments:**
