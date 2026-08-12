namespace TenantPulse.Cli.Commands;

internal static class HelpCommand
{
    public static void Print()
    {
        Console.WriteLine("""
            tenant-pulse — make a CDX demo tenant feel lived-in

            USAGE
              tenant-pulse <command> [options]

            COMMANDS
              doctor            Check configuration, tenant allow-list, enrolment and content provider.
                                Run this first — it tells you exactly what is missing.

              bootstrap         Enrol demo users so they can act. Device-code mode prompts once per
                                user; username/password mode enrols them all unattended.
                    --user <upn>        Enrol a single user.
                    --all               Enrol every eligible user found in the directory.
                    --as <upn>          User whose token is used to read the directory (for --all).

              plan              Show what would happen on a given day. Writes nothing, ever.
                    --date <yyyy-mm-dd> Day to plan (default: today).
                    --days <n>          Plan n consecutive days.
                    --as <upn>          User whose token reads the directory.

              run               Run continuously, performing activity as each moment arrives.
                                DRY RUN unless --live is given.
                    --live              Actually write to the tenant.
                    --as <upn>          User whose token reads the directory.

              once              Execute a small batch immediately, ignoring scheduled times.
                                Useful to prove the pipeline end to end.
                    --count <n>         How many activities (default 5).
                    --kind <kind>       Only this activity kind, e.g. SendMail, CopilotPrompt.
                    --live              Actually write to the tenant.

              verify-copilot    Send a uniquely-marked Copilot prompt as a user, then read the
                                Copilot interaction history back to prove whether API-driven prompts
                                register as real usage. Answers the one thing Microsoft don't document.
                    --user <upn>        User to test with (must hold a Copilot licence).
                    --app-token <jwt>   App-only token with AiEnterpriseInteraction.Read.All.
                    --wait <seconds>    Settle delay before reading history (default 60).

              report            Summarise what tenant-pulse has done.
                    --since <days>      Look back this many days (default 7).
                    --recent <n>        Also list the n most recent activities.

              purge             Delete artefacts tenant-pulse created (mail, files, events).
                    --since <days>      Only purge things created in the last n days (default 30).
                    --live              Actually delete. Without this it only lists.

            GLOBAL OPTIONS
              --config <path>   Config file (default: config/tenant-pulse.json)
              --tenant <id>     Override the target tenant id.
              --seed <n>        Override the simulation seed.
              --live            Disable dry run.
              --dry-run         Force dry run.
              --verbose         Debug logging.

            SAFETY
              Dry run is the default. tenant-pulse refuses to touch any tenant that is not listed in
              Tenant.AllowedTenantIds. Creating the kill-switch file (Simulation.KillSwitchFile,
              default .state/STOP) stops a running simulator within a minute.
            """);
    }
}
