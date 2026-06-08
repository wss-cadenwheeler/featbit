# CP-XX Pod Heart Beat Test

 

**Component:** Control-Plane, Api, Evaluation Server
**Status:** [Draft/Ready/Passed/Failed]

 

## Description
A brief overview of the test case objective and what feature it validates.

 

## Preconditions
- [ ] Start the Control-Plane QA deployment with one of the Quickstart powershell scripts
- [ ] If needed, run Start-PortForwards.ps1 from 01-Infrastructure/ (this depends on which quickstart script is used)
- [ ] East and West Clusters Running in Advanced mode with port forwards and host entries
- [ ] `playground` Organization Exists
- [ ] `contorl-plane-test` Project Exists

 

## Test Steps
1. **Action:** Connect to redis instances using a gui (Redis Insights, Another Redis Desktop Manager Etc.)
2. **Action:** Connect 10 clients to featbit.west.local
3. **Action:** Connect 20 clients to featbit.east.local
4. **Action:** Verify that these clients created in step 2 and 4 are showing up in redis
5. **Action:** Verify that the pod heartbeat is being recorded in redis
6. **Action:** Take down the west pod
7. **Action:** Wait at least 91 seconds or the length of the pod timeout that has been set
8. **Action:** Verify that east pods heart beat has been removed from redis
9. **Action:** Verify that west pod heartbeat and connections are still in redis
10. **Action:** Verify that the clients that were connected to the west pod are now connected to the east pod
11. **Note:** The connections to the east pod will have a different heartbeat and connection id
12. **Action** Bring the west pod back up
13. **Action** Create 10 clients that connect to the new instance of the west pod

 

## Expected Results
- When the pods are up we see the heart beat for the pod updated. The timestamp should be changed each time a new heartbeat comes in
- When a pod is taken down all of the connections should be purged and move over to the other pod
- When a pod comes back up we should see the hearbeat resorted with a new pod id

 

## Post-conditions

 

---
**Notes/Comments:**