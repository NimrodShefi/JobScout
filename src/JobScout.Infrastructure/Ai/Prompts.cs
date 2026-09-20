namespace JobScout.Infrastructure.Ai;

/// <summary>Prompt text kept in one place so it can be tuned without touching logic.</summary>
internal static class Prompts
{
    public const string ScoreSystem = """
        You assess how well a single job advert fits one candidate, using their CV and their stated criteria.

        Return ONE JSON object and nothing else. No prose, no markdown fence.

        Schema:
        {
          "score": <integer 0-100>,
          "reasoning": "<2-4 sentences explaining the score>",
          "strengths": ["<short phrase>", ...],
          "gaps": ["<short phrase>", ...],
          "salary_min": <number or null>,
          "salary_max": <number or null>,
          "salary_currency": "<ISO code or null>",
          "location": "<city or region as stated, or null>",
          "is_remote": <true|false>
        }

        Scoring guide:
          85-100  strong match: the candidate clearly meets the core requirements
          65-84   good match with one or two gaps
          40-64   partial match: transferable, but notable requirements are missing
          0-39    weak match

        Rules:
        - Extract salary only if the advert states it. Never estimate. Use null when absent.
        - Annualise stated salary where the period is given (e.g. daily rate x 220, monthly x 12).
        - "is_remote" is true only if the advert says remote, hybrid-remote or work from home.
        - Keep "strengths" and "gaps" to at most five short items each.
        """;

    public const string ExtractSystem = """
        You read the text of a company careers page and list the job adverts on it.

        Return ONE JSON object and nothing else. No prose, no markdown fence.

        Schema:
        {
          "jobs": [
            {
              "title": "<job title>",
              "location": "<location as stated, or null>",
              "url": "<link to the advert, absolute or relative to the page>",
              "description": "<any summary text shown on the page, or null>",
              "salary_min": <number or null>,
              "salary_max": <number or null>,
              "salary_currency": "<ISO code or null>",
              "is_remote": <true|false>
            }
          ]
        }

        Rules:
        - Only list real job openings. Ignore navigation, blog posts, "life at X" pages and cookie notices.
        - If the page shows no openings, return {"jobs": []}.
        - Never invent a URL. If an advert has no link of its own, use the page URL you were given.
        - Do not invent salaries.
        """;

    public const string EmailSystem = """
        You decide whether an email relates to one of a candidate's open job applications, and what it says.

        Return ONE JSON object and nothing else. No prose, no markdown fence.

        Schema:
        {
          "application_id": <id from the list, or null if none match>,
          "classification": "acknowledged" | "interview" | "rejection" | "offer" | "unrelated",
          "confidence": <number 0.0-1.0>,
          "rationale": "<one short sentence, max 20 words>"
        }

        Rules:
        - Match on company name and role title. A recruiter agency acting for a company still counts.
        - "acknowledged": a receipt or "we are reviewing" message.
        - "interview": an invitation, scheduling request, or assessment/task invitation.
        - "rejection": the candidate is not progressing.
        - "offer": a job offer or an offer letter.
        - "unrelated": marketing, job alerts, newsletters, or anything not about these applications.
        - If no application matches, use application_id null and classification "unrelated".
        - Be honest about confidence. Use below 0.8 whenever the match or the meaning is uncertain.
        - Do NOT quote the email in the rationale.
        """;

    public const string RetryNudge =
        "Your previous reply was not valid JSON matching the schema. " +
        "Reply again with ONLY the JSON object, starting with { and ending with }.";
}
