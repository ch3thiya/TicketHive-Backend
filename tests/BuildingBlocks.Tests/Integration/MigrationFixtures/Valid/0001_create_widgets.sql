CREATE TABLE widgets (
    id UUID PRIMARY KEY,
    name VARCHAR(100) NOT NULL
);

CREATE INDEX idx_widgets_name ON widgets(name);
