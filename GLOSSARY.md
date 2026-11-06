# Studease Feature Intelligence

Turns the feature requests Studease users submit into a small set of underlying Problems that the product team can decide on.

## Language

**Requester**:
A person who submits a Feature Request, identified only by the email address they give with it (there are no accounts).
_Avoid_: Customer, user, submitter, stakeholder

**Feature Request**:
What one Requester submitted, in their own words. It is never edited after submission, links to exactly one Problem, and is what Requesters browse and upvote.
_Avoid_: Ticket, suggestion, feedback item

**Upvote**:
One person's support for an existing Feature Request. Upvoters give no email, so they are not Requesters.
_Avoid_: Vote, +1, like

**Problem**:
The underlying customer need that one or more Feature Requests point to. The product team can rename, merge or split it; Requesters see it only after the team moves it to a status shown on the portal (e.g. In progress).
_Avoid_: Theme, cluster, category, duplicate group

**Category**:
One of seven fixed areas of the Studease app (Finding spots, Adding & editing spots, Spot details, Visits, Profile & account, Social, Other). A Requester picks one when submitting a Feature Request, and that pick is never changed; each Problem has its own Category, which decides where its Feature Requests are listed.
_Avoid_: Tag, type, area, section

**Priority Criteria**:
The written, versioned rules a Problem is scored against: Studease's product goals, the four criteria (Reach, Pain, Strategic fit, Retention risk), what each rating level means, the weights, and the level cut-offs.
_Avoid_: Rubric, scoring rules, framework

**Recommended Priority**:
The AI's suggested level (High, Medium or Low) and 0–100 score for a Problem, worked out against one version of the Priority Criteria.
_Avoid_: AI priority, suggested priority, score

**Priority**:
The level (High, Medium or Low) the product team decides for a Problem; it may differ from the Recommended Priority.
_Avoid_: Rank, importance, severity

**Archive**:
The record of Problems the product team has closed as Shipped or Won't do; their Recommended Priority is frozen at the moment they were closed.
_Avoid_: History, done list, graveyard

**Decision Brief**:
A one-page AI summary of a single Problem, written from all its linked Feature Requests, that helps the product team decide its Priority and status. It summarises the evidence and recommends no decision.
_Avoid_: Brief summary, report, AI summary, recommendation

**Citation**:
A link from a claim in a Decision Brief to the Feature Requests that support it, optionally with words quoted exactly from one of them.
_Avoid_: Source, reference, footnote, evidence link
