# Copilot Instructions

<!-- Add your project-specific rules and context for the AI agent here. -->
- NEVER automatically execute a debug c# service or project through an agent.  ONLY use the agent to test the build.  Debugging and running the service should be done manually by the developer.  The agent should only be used to build the project and run tests, not to execute the service itself.
- If an agent is working and thinking longer than 60s, stop the agent and ask the user if they want to continue or if they want to stop the agent.  Do not let the agent run indefinitely without user input.
- Avoid infinite loops or reverting back and forth between two states.  If you find yourself in a loop, stop and ask the user for guidance on how to proceed.
- When suggesting code changes, always provide a clear explanation of why the change is being made and how it improves the codebase.  This helps the user understand the reasoning behind the change and makes it easier for them to review and approve the change.
- If there are available samples or examples in the codebase, refer to those when suggesting changes or additions.  This helps maintain consistency in the codebase and provides a reference for the user to understand how to implement similar functionality.
