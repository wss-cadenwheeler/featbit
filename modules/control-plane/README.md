# Featbit Control Plane

The **control-plane** is the management API for the FeatBit platform. When enabled, it functions as the central hub for managing messaging between the API and evaluation servers and any additional functionality that should be executed prior to forwarding the messages.

## What it does

- Consumes messages from the API server for flag changes, segment changes, license updates, and secret updates. Prior to forwarding these messages, it is capable of updating all Redis instances with the upsert that the API server performs.
- Forward the previously consumed messages to the evaluation servers.
- Serves an endpoint used to force a full-sync of all clients connected to all instances of the evaluation servers.