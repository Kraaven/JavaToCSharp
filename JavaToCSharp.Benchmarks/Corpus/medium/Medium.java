package com.example.medium;

import java.util.List;
import java.util.Map;
import java.util.HashMap;
import java.util.stream.Collectors;

public class Medium {

    public abstract static class Shape {
        protected final String name;

        protected Shape(String name) {
            this.name = name;
        }

        public abstract double area();

        public String describe() {
            return name + " area=" + area();
        }
    }

    public static class Circle extends Shape {
        private final double radius;

        public Circle(double radius) {
            super("circle");
            this.radius = radius;
        }

        @Override
        public double area() {
            return Math.PI * radius * radius;
        }
    }

    public static class Rectangle extends Shape {
        private final double width;
        private final double height;

        public Rectangle(double width, double height) {
            super("rectangle");
            this.width = width;
            this.height = height;
        }

        @Override
        public double area() {
            return width * height;
        }
    }

    public enum Operation {
        ADD, SUBTRACT, MULTIPLY, DIVIDE;

        public double apply(double a, double b) {
            return switch (this) {
                case ADD -> a + b;
                case SUBTRACT -> a - b;
                case MULTIPLY -> a * b;
                case DIVIDE -> a / b;
            };
        }
    }

    public static double calculate(Operation op, double a, double b) {
        return op.apply(a, b);
    }

    public static List<String> transform(List<String> input) {
        return input.stream()
            .map(String::toUpperCase)
            .filter(s -> !s.isEmpty())
            .sorted()
            .collect(Collectors.toList());
    }

    public static Map<String, Integer> countWords(String text) {
        Map<String, Integer> counts = new HashMap<>();
        for (String word : text.split("\\s+")) {
            counts.merge(word, 1, Integer::sum);
        }
        return counts;
    }

    public static void main(String[] args) throws Exception {
        List<Shape> shapes = new java.util.ArrayList<>();
        shapes.add(new Circle(2.0));
        shapes.add(new Rectangle(3.0, 4.0));

        try (java.io.BufferedReader reader = new java.io.BufferedReader(new java.io.InputStreamReader(System.in))) {
            String line = reader.readLine();
            if (line != null) {
                System.out.println(line.length());
            }
        }

        double total = 0;
        for (Shape shape : shapes) {
            total += shape.area();
            System.out.println(shape.describe());
        }

        System.out.println("total=" + total);
        System.out.println(calculate(Operation.ADD, 1, 2));
    }
}
