## Background
You are to implement a metadata API server. This is simply an API to manage two distinct resources: Application and Maintainer. Maintainers are users that manage Application. They can perform operations on these applications such as creation, deletion, get, and update. Maintainers can themselves are managed via the same operations. This can be a small business application, where internal users want to maange their applications. But it can also be a large scale application, (for example Github), where we need to support hundreds of millions of maintainers and applications. For the purposes of this task, we will be using .NET and C#.

## Scope
A production ready RESTful HTTP API that enables basic CRUD operations for Application and Maintainer resources. The flow should look as follows: A new maintainer is onboarded maintainer creation. They get a name, email, and automatically generated Id upon creation. New applications can be created that are associated with a maintainer. Existing applications can be associated with a maintainer as well. These applications simply contain metadata of a web application. Maintainers should be able to form all the CRUD operations on these applications, and should only be able to do so for the applications they are associated with. **TODO consider auth here**

## Data Model

Notes:
- Added Guid ID to maintainer to ensure robust uniqueness

type Maintainer struct {
    Name  string
    Email string
}

Notes:
- Added reference to maintainer to correlate application with given maintainer
- **TODO Should we have appId? but then address concern about idempotency, since creation is done by providing title...**
- **May need to add additional fields depending on production readiness needs?**

type Application struct {
    Title           string
    Version         string
    Company         string
    Website         string
    Source          string
    License         string
    Description     string
}

type AppAssociation struct{
    maintainerId string
    appId string
}

### Data Model Input Validation
Maintainer name requirements (look up the standard) and email format validation. Have a standard versioning numbering scheme for Application, validate website URL. **TODO figure out what Source, License should look like, conventionally**?

## Technical Requirements
- Deployed RESTful HTTP API Server with CRUD operations for both resources

## API Design
There will be a many-to-many relationship between Maintainers and Applications. A maintainer can manage zero or more applications. And a given application can have zero or ore maintainers. In other words, applications can be maintained by more than one maintainer. And a given maintainer can manage multiple applications This relationship is represented by separate data model, AppAssociation.

**TODO format this via AI**
### Maintainer
**TODO consider if we need backing Id or if name is sufficient ID**
- CREATE /maintainer/{name}
    request: {
        email: "myemail@provider.com
    }
    200 OK response:{
        maintainerId: GUID,
        name: "myName",
        email: "myemail@provider.com"
    }
- GET /maintainer/{name}
- PUT/PATCH /maintainer/{name}
    request:{
        name
        email
    }
- DELETE /maintainer/{name}

### Application
- CREATE /application/{title}
    request:{
        version
        company
        website
        source
        license
        desc  
    }
- GET /application/{title}
- PUT/PATCH /application/{title}
- DELETE /application/{title}
- POST /application/associate/{title}/maintainer/{name} 

### AppAssociation
**is this conventional?? probably not**
- POST /create/appAssociation/maintainer/{name}/application/{title}

**TODO any additional API operations? is it really this simple?**

## Storage Layer
For scalability and built-in production readiness, an externally hosted database is what we will be using for this. 
**investigate options with AI, such as what is embedded DB?, what meets the scale requirements for this API? it's not a particularly complex datamodel...**

## Production Readiness
- Monitoring, telemetry, alerting **(is there a centralized solution like datadog for this I can easily hookup?)**
- Auth (for maintainer + app access). Build an extendable layer for this?
- API spec (OPEN API link)
- Version control (model after how our service does it with attribute tags)
- Retries, throttling, timeouts, exception handling
- **What is OOM?, is this a major concern?**
- **What wmight optimistic concurrency look like in this application?**
- **TODO brainstorm more production readiness features**

## Testing
- Unit tests
- Synthetic tests making calls against the real API **(Github Actions pipeline?)** 

**TODO any other KEY design decisions to consider?**


    