# MailZort

MailZort is a .NET 7 worker project designed to run in a Docker container, functioning as a scheduled job. It connects to a configured email folder and sorts incoming mail based on user-defined rules. I use it with Kubernetes cron jobs Manifest below,

## Notes
- Emails get cached in an SQLite database thats put in the `data` folder in execution directory
- Appsetting.json file in the project gets copied to `data` folder in execution directory

## Table of Contents


## Features

- Automatically sorts incoming emails based on a flexible configuration.
- Runs as a .NET 7 worker project in a Docker container.
- Ideal for handling email categorization and organization tasks.

## Getting Started

### Prerequisites

Before you can use MailZort, you need to have the following dependencies installed:

- [.NET 7](https://dotnet.microsoft.com/download/dotnet/7.0)

### Installation

1. Clone the MailZort repository to your local machine:

   ```bash
   git clone https://github.com/your-username/mailzort.git


#Usage
##Configuration
MailZort requires a configuration file named appSettings.json. Here's an example of a configuration file:

``` json
{
    "EmailSettings": {
        //Email Server - Host
         "Server": "mymail.com",
        //Email Server - Port
        "Port": 993,
         //Email Server - UserName
        "UserName": "myuser",
        //Email Server - Password
        "Password": "apassword",
        //Email Server - Should we use SSL
        "UseSsl": true,
        // Determines whether MailZort should pull all emails or use the email cache
        "PullMails": true,
        // The folder where deleted messages will be moved to.
        "Trash": "Deleted Messages",
        "TestMode": false,
        // When we pull messages , how many should we pull: "all", "not-seen", "recent"
        "SearchMode": "recent"
    },
    "rules": [
        {
            "Name": "bills",
            // What folder to look in
            "Folder": "Inbox",
            // How old a message should be before moving it
             "DaysOld": 1,
            // Folder to move matches too
            "MoveTo": "Bills",
            // What areas should we look for a value
            // Options: "All," "Subject," "Body," "Sender," "Recipient," "SenderEmail"
            "LookIn": 3,
             // Determines the type of expression to match values.
            // Options: "Contains," "DoesNotContain," "Is," "IsNot," "StartsWith," "EndsWith," "MatchesRegex," "DoesNotMatchRegex"
            "ExpressionType": 0,
             // Values to match in the email content.
            "Values": [
                "AT&T",
                "Amazon",
                "Steam"
            }
        },
        // Add more rules here...
    ]
}



```